using InfraMapper.Services.Agent.Runtime;
using InfraMapper.Services.Agent.Tools;
using Microsoft.SemanticKernel.Agents;

namespace InfraMapper.Services.Agent.SubAgents;

public sealed class InvestigatorAgent
{
    private readonly SkAgentFactory _agentFactory;
    private readonly AzureResourceService _resourceService;
    private readonly IArmGenericResourceService _genericResources;

    public InvestigatorAgent(
        SkAgentFactory agentFactory,
        AzureResourceService resourceService,
        IArmGenericResourceService genericResources)
    {
        _agentFactory = agentFactory;
        _resourceService = resourceService;
        _genericResources = genericResources;
    }

    public ChatCompletionAgent Build(object? clarificationPlugin = null)
    {
        var tools = new InvestigatorTools(_resourceService, _genericResources);
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
        • If investigation is blocked by a human choice, call ask_clarifying_question and then
          output ONLY its raw JSON result. Do not ask user-facing questions in prose.
        • Your final response is the investigation summary text. No raw JSON in the final response.
        • Do NOT use emojis.
        """;
}
