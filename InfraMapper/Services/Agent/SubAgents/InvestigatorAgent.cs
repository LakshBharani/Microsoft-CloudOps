using Anthropic;
using InfraMapper.Services.Agent.AgentFramework;
using InfraMapper.Services.Agent.Tools;

namespace InfraMapper.Services.Agent.SubAgents;

/// <summary>
/// Builds the InvestigatorAgent (Haiku 4.5) which performs Azure resource discovery.
/// Exposed to the Orchestrator as the "investigate_infrastructure" agent-tool.
///
/// The Investigator self-reflects: if the initial query is insufficient for the focus area,
/// it queries again before returning. This keeps large Resource Graph payloads out of the
/// Orchestrator's context.
/// </summary>
public sealed class InvestigatorAgent
{
    private readonly AnthropicClient _client;
    private readonly AzureResourceService _resourceService;
    private readonly IArmGenericResourceService _genericResources;

    public InvestigatorAgent(
        AnthropicClient client,
        AzureResourceService resourceService,
        IArmGenericResourceService genericResources)
    {
        _client = client;
        _resourceService = resourceService;
        _genericResources = genericResources;
    }

    /// <summary>Builds the InvestigatorAgent and its "investigate_infrastructure" AgentTool.</summary>
    public (AnthropicAgent Agent, AgentTool Function) Build()
    {
        var tools = new InvestigatorTools(_resourceService, _genericResources);
        var agentTools = BuildAgentTools(tools);

        var agent = new AnthropicAgent(
            _client,
            AgentRegistry.GetModel("investigator"),
            SystemPrompt,
            agentTools);

        var function = new AgentTool
        {
            Name = "investigate_infrastructure",
            Description =
                "Investigate Azure infrastructure for a given focus area (e.g. 'storage accounts', 'VMs in westus2'). " +
                "Returns a structured summary of existing resources and their dependencies relevant to the focus. " +
                "Call this before plan_deployment to understand what already exists.",
            InputSchema = """{"type":"object","properties":{"focus":{"type":"string","description":"The infrastructure focus area to investigate"},"subscription_id":{"type":"string","description":"Optional subscription ID override"}},"required":["focus"]}""",
            Invoke = async (argsJson, ct) =>
            {
                var message = BuildUserMessage(argsJson);
                return await agent.RunAsync(message, ct);
            }
        };

        return (agent, function);
    }

    private static string BuildUserMessage(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return "Investigate the infrastructure.";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            var focus = root.TryGetProperty("focus", out var f) ? f.GetString() : null;
            var subId = root.TryGetProperty("subscription_id", out var s) ? s.GetString() : null;
            var msg = $"Investigate infrastructure for: {focus ?? "general overview"}";
            if (!string.IsNullOrWhiteSpace(subId)) msg += $". Subscription: {subId}";
            return msg;
        }
        catch { return "Investigate the infrastructure."; }
    }

    private static IList<AgentTool> BuildAgentTools(InvestigatorTools tools)
    {
        return
        [
            AgentToolFactory.Create(tools.GetInfrastructureGraphAsync,
                "get_infrastructure_graph",
                "Get the full Azure infrastructure graph for a subscription."),
            AgentToolFactory.Create(tools.GetResourceAsync,
                "get_resource",
                "Get details of a specific Azure resource by ARM ID."),
        ];
    }

    private const string SystemPrompt = """
        You are InfraMapper Investigator — an Azure resource discovery specialist.

        When asked to investigate infrastructure:
        1. Call get_infrastructure_graph to retrieve all resources in the subscription.
        2. SELF-REFLECT: Is the returned data sufficient to answer the investigation focus?
           - If the graph contains errors or is clearly incomplete, call get_infrastructure_graph again.
           - If you need details on a specific resource, call get_resource with its full ARM ID.
        3. Produce a concise, structured summary focused on the investigation topic. Include:
           • Existing resources of the relevant type (name, location, SKU, resource group)
           • Related dependencies (e.g. VNets, NSGs, storage accounts a VM uses)
           • Notable gaps or potential conflicts (e.g. naming conflicts, region mismatches)
           • Resource count and utilization summary if available

        CRITICAL RULES:
        • Do NOT include resources unrelated to the investigation focus.
        • Keep the summary concise — the Planner will use it, so signal what matters, not raw JSON.
        • If a query returns a transient error, retry once before reporting failure.
        • Your final response is the investigation summary text. No raw JSON in the final response.
        • Do NOT use emojis.
        """;
}
