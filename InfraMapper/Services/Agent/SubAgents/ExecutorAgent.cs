using InfraMapper.Services.Agent.Runtime;
using InfraMapper.Services.Agent.Tools;
using Microsoft.SemanticKernel.Agents;

namespace InfraMapper.Services.Agent.SubAgents;

public sealed class ExecutorAgent
{
    private readonly SkAgentFactory _agentFactory;
    private readonly IArmDeploymentService _deploymentService;
    private readonly IApprovalService _approvalService;
    private readonly IResourceMutationApprovalService _mutationApprovals;
    private readonly IArmGenericResourceService _genericResources;
    private readonly PlanStore _planStore;

    public ExecutorAgent(
        SkAgentFactory agentFactory,
        IArmDeploymentService deploymentService,
        IApprovalService approvalService,
        IResourceMutationApprovalService mutationApprovals,
        IArmGenericResourceService genericResources,
        PlanStore planStore)
    {
        _agentFactory = agentFactory;
        _deploymentService = deploymentService;
        _approvalService = approvalService;
        _mutationApprovals = mutationApprovals;
        _genericResources = genericResources;
        _planStore = planStore;
    }

    public ChatCompletionAgent BuildForSession(
        string sessionId,
        string subscriptionId,
        object? clarificationPlugin = null)
    {
        var tools = new ExecutorTools(
            _deploymentService, _approvalService, _mutationApprovals,
            _genericResources, _planStore, sessionId, subscriptionId);

        var plugins = new List<(object Plugin, string Name)> { (tools, "executor") };
        if (clarificationPlugin is not null)
            plugins.Add((clarificationPlugin, "clarification"));

        return _agentFactory.Create(
            "executor",
            SystemPrompt,
            plugins.ToArray());
    }

    public static string BuildUserMessage(string planId, string? clarificationAnswers = null)
    {
        var msg = $"Apply the approved Azure plan. plan_id={planId}. " +
                  $"You MUST pass plan_id=\"{planId}\" as an argument to every tool call.";
        if (!string.IsNullOrWhiteSpace(clarificationAnswers))
            msg += clarificationAnswers;
        return msg;
    }

    private const string SystemPrompt = """
        You are InfraMapper Executor. Apply approved Azure deployment plans to Azure.

        When asked to apply a plan:

        STEP 1 — RETRIEVE THE PLAN
        Call get_plan_details(plan_id) to read the full plan. It returns a JSON object with:
          • title: deployment description
          • operations: array of { action, resource_type, resource_name, resource_group, details }
          • risk_level: Low / Medium / High

        STEP 2 — CHOOSE APPLY STRATEGY
        Inspect the operations:

          RESOURCE REMOVAL operations only:
          → Call apply_resource_mutation for each operation with operation="Delete".
            Construct the ARM resource ID from resource_type + resource_name + resource_group.
            ARM resource ID format: /subscriptions/{sub}/resourceGroups/{rg}/providers/{type}/{name}

          CREATE / UPDATE / DEPLOY operations:
          → Construct a complete ARM template JSON from the operations list. Include:
              - $schema, contentVersion, resources array
              - For each operation: type, name, apiVersion, location, properties, sku (from details field)
              - dependsOn arrays matching dependency order in the plan
          → Call deploy_arm_template with the plan_id, a deployment name, and the constructed templateJson.
            Use resource_group from the first resource that has one; use subscription-scope if creating an RG.
            Do not invent or pass plan_id as a subscription ID. Backend will use the session subscription.

        STEP 3 — HANDLE RESULTS
        • If result has needs_replan:true, return the full error JSON without another attempt.
        • If result has error_type:"transient", retry once.
        • If deployment succeeds, return the result JSON including deployment_name.

        CRITICAL:
        • Always use the plan_id from the plan as the approved plan_id for write tools.
        • plan_id is not a subscription_id and must not appear inside /subscriptions/{...} resource IDs.
        • Final response must be the raw JSON result of the last tool call. No other text.
        • Write tools require a valid approved plan_id.
        • If a human choice is needed, call ask_clarifying_question and output only its raw JSON result.
        • Use plain text only.
        """;
}
