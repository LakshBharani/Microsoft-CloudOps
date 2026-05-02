using Anthropic;
using InfraMapper.Services.Agent.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

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
    private readonly IAnthropicClient _client;
    private readonly IArmDeploymentService _deploymentService;
    private readonly IApprovalService _approvalService;
    private readonly IResourceMutationApprovalService _mutationApprovals;
    private readonly IArmGenericResourceService _genericResources;
    private readonly PlanStore _planStore;

    public ExecutorAgent(
        IAnthropicClient client,
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

    /// <summary>Builds the ExecutorAgent and its "execute_plan" AIFunction for the given session.</summary>
    public (AIAgent Agent, AIFunction Function) BuildForSession(string sessionId)
    {
        var tools = new ExecutorTools(
            _deploymentService, _approvalService, _mutationApprovals,
            _genericResources, _planStore, sessionId);

        var aiTools = BuildAiTools(tools);

        var agent = _client.AsAIAgent(
            model: AgentRegistry.GetModel("executor"),
            instructions: SystemPrompt,
            name: "InfraMapperExecutor",
            description: "Applies approved ARM deployment plans to Azure.",
            tools: aiTools);

        var function = agent.AsAIFunction(new AIFunctionFactoryOptions
        {
            Name = "execute_plan",
            Description =
                "Apply an approved deployment plan to Azure. " +
                "Returns success:true when complete, or needs_replan:true with error details " +
                "if the template is invalid and the plan must be revised. " +
                "Only call this after the user has approved the plan.",
        });

        return (agent, function);
    }

    private static IList<AITool> BuildAiTools(ExecutorTools tools)
    {
        var opts = OrchestratorTools.SnakeCaseOpts;
        return
        [
            AIFunctionFactory.Create(tools.DeployArmTemplateAsync,
                new AIFunctionFactoryOptions { Name = "deploy_arm_template", SerializerOptions = opts }),
            AIFunctionFactory.Create(tools.ApplyResourceMutationAsync,
                new AIFunctionFactoryOptions { Name = "apply_resource_mutation", SerializerOptions = opts }),
            AIFunctionFactory.Create(tools.GetDeploymentStatusAsync,
                new AIFunctionFactoryOptions { Name = "get_deployment_status", SerializerOptions = opts }),
        ];
    }

    private const string SystemPrompt = """
        You are InfraMapper Executor — you apply approved Azure deployment plans.

        When asked to execute a plan:
        1. Call deploy_arm_template or apply_resource_mutation with the plan_id and required parameters.
        2. If the call returns needs_replan:true, do NOT retry. Return the full error JSON as your response
           so the Orchestrator can send the intent back to the Planner for revision.
        3. If the call returns a transient error (error_type:"transient"), wait and retry once.
        4. If deployment succeeds, return the deployment result including the deployment_name.

        CRITICAL:
        • You may only call write tools with an approved plan_id. Never bypass plan approval.
        • After the operation completes, your response MUST be the raw JSON result of the tool call.
        • If needs_replan:true, output ONLY that JSON so the Orchestrator knows to re-plan.
        """;
}
