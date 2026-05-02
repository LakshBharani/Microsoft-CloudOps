using Anthropic;
using InfraMapper.Services;
using InfraMapper.Services.Agent.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

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
    private readonly IAnthropicClient _client;
    private readonly AzureResourceService _resourceService;
    private readonly IArmGenericResourceService _genericResources;

    public InvestigatorAgent(
        IAnthropicClient client,
        AzureResourceService resourceService,
        IArmGenericResourceService genericResources)
    {
        _client = client;
        _resourceService = resourceService;
        _genericResources = genericResources;
    }

    /// <summary>Builds the InvestigatorAgent and its "investigate_infrastructure" AIFunction.</summary>
    public (AIAgent Agent, AIFunction Function) Build()
    {
        var tools = new InvestigatorTools(_resourceService, _genericResources);
        var aiTools = BuildAiTools(tools);

        var agent = _client.AsAIAgent(
            model: AgentRegistry.GetModel("investigator"),
            instructions: SystemPrompt,
            name: "InfraMapperInvestigator",
            description: "Discovers and summarizes Azure resources relevant to a given focus area.",
            tools: aiTools);

        var function = agent.AsAIFunction(new AIFunctionFactoryOptions
        {
            Name = "investigate_infrastructure",
            Description =
                "Investigate Azure infrastructure for a given focus area (e.g. 'storage accounts', 'VMs in westus2'). " +
                "Returns a structured summary of existing resources and their dependencies relevant to the focus. " +
                "Call this before plan_deployment to understand what already exists.",
        });

        return (agent, function);
    }

    private static IList<AITool> BuildAiTools(InvestigatorTools tools)
    {
        var opts = OrchestratorTools.SnakeCaseOpts;
        return
        [
            AIFunctionFactory.Create(tools.GetInfrastructureGraphAsync,
                new AIFunctionFactoryOptions { Name = "get_infrastructure_graph", SerializerOptions = opts }),
            AIFunctionFactory.Create(tools.GetResourceAsync,
                new AIFunctionFactoryOptions { Name = "get_resource", SerializerOptions = opts }),
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
        """;
}
