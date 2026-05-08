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
          • template_json: complete deployable ARM template JSON for create/update/deploy plans
          • parameters_json, resource_group_name, location, deployment_name

        STEP 2 — CHOOSE APPLY STRATEGY
        Inspect the plan:

          PLAN HAS template_json:
          → Deploy template_json exactly as approved by calling deploy_arm_template.
            Use parameters_json if present, otherwise "{}".
            Use deployment_name from the plan if present, otherwise choose "deployment-{shortPlanId}".
            Use resource_group_name from the plan for resource-group-scoped templates.
            Use location from the plan for subscription-scoped templates.
            Do not rewrite, simplify, or regenerate the template.

          PLAN HAS NO template_json:
          → This is allowed only for delete-only plans or legacy fallback operations.

          RESOURCE REMOVAL operations only:
          → Call apply_resource_mutation for each operation with operation="Delete".
            Construct the ARM resource ID from resource_type + resource_name + resource_group.
            ARM resource ID format: /subscriptions/{sub}/resourceGroups/{rg}/providers/{type}/{name}

          CREATE / UPDATE / DEPLOY operations without template_json:
          → Return {"error":true,"error_type":"missing_template_json","needs_replan":true,
             "message":"Approved plan has mutating operations but no template_json."}

        STEP 3 — HANDLE RESULTS
        • If result has needs_replan:true, return the full error JSON without another attempt.
        • If result has error_type:"transient", retry once.
        • If deployment succeeds, return the result JSON including deployment_name.
        • If no write tool was called, or the result is not structured JSON, return:
          {"error":true,"error_type":"executor_no_result","needs_replan":true,"message":"Executor could not produce a structured deployment result."}

        CRITICAL:
        • Always use the plan_id from the plan as the approved plan_id for write tools.
        • plan_id is not a subscription_id and must not appear inside /subscriptions/{...} resource IDs.
        • Final response must be the raw JSON result of the last tool call. No other text.
        • Write tools require a valid approved plan_id.
        • If a human choice is needed, call ask_clarifying_question and output only its raw JSON result.
        • Use plain text only.
        """;
}
