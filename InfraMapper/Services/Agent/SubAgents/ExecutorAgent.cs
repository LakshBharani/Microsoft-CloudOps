using Anthropic;
using InfraMapper.Services.Agent.AgentFramework;
using InfraMapper.Services.Agent.Tools;

namespace InfraMapper.Services.Agent.SubAgents;

/// <summary>
/// Builds the ExecutorAgent (Haiku 4.5) which applies approved deployment plans to Azure.
/// Exposed to the Orchestrator as the "execute_plan" agent-tool.
///
/// Error handling within the Executor:
///   - Transient errors (429/503) are retried in code (up to 3 attempts).
///   - Validation/quota errors return needs_replan:true — the Orchestrator loops back to plan_deployment.
/// </summary>
public sealed class ExecutorAgent
{
    private readonly AnthropicClient _client;
    private readonly IArmDeploymentService _deploymentService;
    private readonly IApprovalService _approvalService;
    private readonly IResourceMutationApprovalService _mutationApprovals;
    private readonly IArmGenericResourceService _genericResources;
    private readonly PlanStore _planStore;

    public ExecutorAgent(
        AnthropicClient client,
        IArmDeploymentService deploymentService,
        IApprovalService approvalService,
        IResourceMutationApprovalService mutationApprovals,
        IArmGenericResourceService genericResources,
        PlanStore planStore)
    {
        _client = client;
        _deploymentService = deploymentService;
        _approvalService = approvalService;
        _mutationApprovals = mutationApprovals;
        _genericResources = genericResources;
        _planStore = planStore;
    }

    /// <summary>Builds the ExecutorAgent and its "execute_plan" AgentTool for the given session.</summary>
    public (AnthropicAgent Agent, AgentTool Function) BuildForSession(string sessionId, string subscriptionId)
    {
        var tools = new ExecutorTools(
            _deploymentService, _approvalService, _mutationApprovals,
            _genericResources, _planStore, sessionId, subscriptionId);

        var agentTools = BuildAgentTools(tools);

        var agent = new AnthropicAgent(
            _client,
            AgentRegistry.GetModel("executor"),
            SystemPrompt,
            agentTools);

        var function = new AgentTool
        {
            Name = "execute_plan",
            Description =
                "Apply an approved deployment plan to Azure. " +
                "Returns success:true when complete, or needs_replan:true with error details " +
                "if the template is invalid and the plan must be revised. " +
                "Only call this after the user has approved the plan.",
            InputSchema = """{"type":"object","properties":{"plan_id":{"type":"string","description":"The approved plan_id to execute"}},"required":["plan_id"]}""",
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
        if (string.IsNullOrWhiteSpace(argsJson)) return "Execute the approved plan.";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            var planId = root.TryGetProperty("plan_id", out var p) ? p.GetString() : null;
            return $"Execute plan with id: {planId ?? "latest"}";
        }
        catch { return "Execute the approved plan."; }
    }

    private static IList<AgentTool> BuildAgentTools(ExecutorTools tools)
    {
        return
        [
            AgentToolFactory.Create(tools.GetPlanDetails,
                "get_plan_details",
                "Retrieve the full plan including all operations. Call this first to know what to deploy."),
            AgentToolFactory.Create(tools.DeployArmTemplateAsync,
                "deploy_arm_template",
                "Deploy an ARM template to Azure. Requires an approved plan_id."),
            AgentToolFactory.Create(tools.ApplyResourceMutationAsync,
                "apply_resource_mutation",
                "Apply a resource mutation (update/delete) to Azure. Requires an approved plan_id."),
            AgentToolFactory.Create(tools.GetDeploymentStatusAsync,
                "get_deployment_status",
                "Get the status of an Azure deployment by name."),
        ];
    }

    private const string SystemPrompt = """
        You are InfraMapper Executor — you apply approved Azure deployment plans to Azure.

        When asked to execute a plan:

        STEP 1 — RETRIEVE THE PLAN
        Call get_plan_details(plan_id) to read the full plan. It returns a JSON object with:
          • title: deployment description
          • operations: array of { action, resource_type, resource_name, resource_group, details }
          • risk_level: Low / Medium / High

        STEP 2 — CHOOSE EXECUTION STRATEGY
        Inspect the operations:

          DELETE operations only:
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
        • If result has needs_replan:true, return the full error JSON. Do NOT retry.
        • If result has error_type:"transient", retry once.
        • If deployment succeeds, return the result JSON including deployment_name.

        CRITICAL:
        • Always use the plan_id from the plan as the approved plan_id for write tools.
        • Never use plan_id as subscription_id or inside /subscriptions/{...} resource IDs.
        • Your final response MUST be the raw JSON result of the last tool call. No other text.
        • Never call write tools without a valid approved plan_id.
        • Do NOT use emojis.
        """;
}
