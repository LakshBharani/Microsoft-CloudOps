using InfraMapper.Services.Agent.Runtime;
using InfraMapper.Services.Agent.Tools;
using InfraMapper.Services;
using Microsoft.SemanticKernel.Agents;

namespace InfraMapper.Services.Agent.SubAgents;

public sealed class InvestigatorAgent
{
    private readonly SkAgentFactory _agentFactory;
    private readonly AzureResourceService _resourceService;
    private readonly IArmGenericResourceService _genericResources;
    private readonly ArmExistenceProbe _existenceProbe;

    public InvestigatorAgent(
        SkAgentFactory agentFactory,
        AzureResourceService resourceService,
        IArmGenericResourceService genericResources,
        ArmExistenceProbe existenceProbe)
    {
        _agentFactory = agentFactory;
        _resourceService = resourceService;
        _genericResources = genericResources;
        _existenceProbe = existenceProbe;
    }

    public ChatCompletionAgent Build(object? clarificationPlugin = null)
    {
        var tools = new InvestigatorTools(_resourceService, _genericResources, _existenceProbe);
        var plugins = new List<(object Plugin, string Name)> { (tools, "investigator") };
        if (clarificationPlugin is not null)
            plugins.Add((clarificationPlugin, "clarification"));

        return _agentFactory.Create(
            "investigator",
            SystemPrompt,
            plugins.ToArray());
    }

    public static string BuildUserMessage(string focus, string? subscriptionId = null)
    {
        var msg = $"Investigate infrastructure for: {focus}";
        if (!string.IsNullOrWhiteSpace(subscriptionId))
            msg += $". Subscription: {subscriptionId}";
        return msg;
    }

    private const string SystemPrompt = """
        You are InfraMapper Investigator — an Azure resource discovery specialist.

        When asked to investigate infrastructure:
        1. PREFER targeted checks over a full graph dump:
           • If the focus mentions a specific resource group, call check_resource_group_exists.
           • If the focus mentions specific resource names, call check_resource_exists for each
             with the full ARM resource ID. Run these checks in parallel when possible.
           • Only call get_infrastructure_graph when you need a broad sweep (e.g. discover all
             resources in a subscription or RG with no specific names). Optionally pass the
             resourceGroup argument to scope the query.
        2. SELF-REFLECT: Is the returned data sufficient to answer the investigation focus?
           - Do NOT call the same tool with the same args twice. One probe per resource.
           - If a check returned 403/azure_api/transient, retry once. Otherwise accept the result.
           - If you need full details on a found resource, call get_resource with its full ARM ID.
        3. Produce a concise, structured summary focused on the investigation topic. Include:
           • Existing resources of the relevant type (name, location, SKU, resource group)
           • Related dependencies (e.g. VNets, NSGs, storage accounts a VM uses)
           • Notable gaps or potential conflicts (e.g. naming conflicts, region mismatches)
           • Resource count and utilization summary if available

        CRITICAL RULES:
        • Do NOT include resources unrelated to the investigation focus.
        • Keep the summary concise — the Planner will use it, so signal what matters, not raw JSON.
        • If a query returns a transient error, retry once before reporting failure.
        • If investigation is blocked by a human choice, call ask_clarifying_question and then
          output ONLY its raw JSON result. Do not ask user-facing questions in prose.
        • Your final response is the investigation summary text. No raw JSON in the final response.
        • Do NOT use emojis.
        """;
}
