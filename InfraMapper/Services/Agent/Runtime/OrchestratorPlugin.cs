using System.ComponentModel;
using InfraMapper.Services.Agent.SubAgents;
using InfraMapper.Services.Agent.Tools;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;

namespace InfraMapper.Services.Agent.Runtime;

public sealed class OrchestratorPlugin
{
    private readonly SkAgentRunner _runner;
    private readonly ChatCompletionAgent _investigator;
    private readonly ChatCompletionAgent _planner;
    private readonly PlannerTools _plannerTools;
    private readonly ChatCompletionAgent _critic;
    private readonly ChatCompletionAgent _questioner;
    private readonly ChatCompletionAgent _executor;
    private readonly ChatCompletionAgent _reflector;

    public OrchestratorPlugin(
        SkAgentRunner runner,
        ChatCompletionAgent investigator,
        ChatCompletionAgent planner,
        PlannerTools plannerTools,
        ChatCompletionAgent critic,
        ChatCompletionAgent questioner,
        ChatCompletionAgent executor,
        ChatCompletionAgent reflector)
    {
        _runner = runner;
        _investigator = investigator;
        _planner = planner;
        _plannerTools = plannerTools;
        _critic = critic;
        _questioner = questioner;
        _executor = executor;
        _reflector = reflector;
    }

    [KernelFunction("investigate_infrastructure")]
    [Description("Investigate Azure infrastructure for a focus area and return a focused summary of relevant resources and dependencies. Call before planning when current state matters.")]
    public Task<string> InvestigateInfrastructure(
        [Description("The infrastructure focus area to investigate")] string focus,
        [Description("Optional subscription ID override")] string? subscription_id = null,
        CancellationToken cancellationToken = default) =>
        _runner.RunAsync(_investigator, InvestigatorAgent.BuildUserMessage(focus, subscription_id), cancellationToken);

    [KernelFunction("plan_deployment")]
    [Description("Generate a complete Azure deployment/change plan for the user intent. Returns plan JSON including plan_id, title, operations, and risk_level.")]
    public Task<string> PlanDeployment(
        [Description("What the user wants to deploy or change")] string intent,
        [Description("Optional summary from investigate_infrastructure")] string? investigator_summary = null,
        CancellationToken cancellationToken = default)
    {
        _plannerTools.BeginPlan(intent);
        return _runner.RunAsync(_planner, PlannerAgent.BuildUserMessage(intent, investigator_summary), cancellationToken);
    }

    [KernelFunction("critique_plan")]
    [Description("Validate a deployment plan for correctness, naming rules, dependency ordering, region/SKU compatibility, and security.")]
    public Task<string> CritiquePlan(
        [Description("The plan_id to critique")] string plan_id,
        CancellationToken cancellationToken = default) =>
        _runner.RunAsync(_critic, CriticAgent.BuildUserMessage(plan_id), cancellationToken);

    [KernelFunction("ask_clarifying_question")]
    [Description("Ask the user a targeted clarification question when planning is blocked by ambiguity or critic feedback requires a human choice.")]
    public Task<string> AskClarifyingQuestion(
        [Description("Why a user choice is needed")] string context,
        [Description("Recommended default choice if known")] string? recommended_default = null,
        [Description("general, name_correction, scope_confirmation, scope_exclusions, or business_reason")] string category = "general",
        [Description("Destructive or preference scope this answer applies to, if any")] string? confirmation_scope = null,
        [Description("Agent that needs the answer")] string originating_agent = "orchestrator",
        CancellationToken cancellationToken = default) =>
        _runner.RunAsync(
            _questioner,
            QuestionerAgent.BuildUserMessage(context, recommended_default, category, confirmation_scope, originating_agent),
            cancellationToken);

    [KernelFunction("execute_plan")]
    [Description("Apply an approved deployment plan to Azure. Only call after the user has approved the plan.")]
    public async Task<string> ExecutePlan(
        [Description("The approved plan_id to execute")] string plan_id,
        CancellationToken cancellationToken = default)
    {
        using var scope = ExecutorTools.UsePlan(plan_id);
        return await _runner.RunAsync(_executor, ExecutorAgent.BuildUserMessage(plan_id), cancellationToken);
    }

    [KernelFunction("reflect_on_deployment")]
    [Description("Audit a completed deployment and record lessons for future planning. Call after every execute_plan.")]
    public Task<string> ReflectOnDeployment(
        [Description("Summary of what was deployed, what succeeded, and what failed")] string summary,
        CancellationToken cancellationToken = default) =>
        _runner.RunAsync(_reflector, ReflectorAgent.BuildUserMessage(summary), cancellationToken);
}
