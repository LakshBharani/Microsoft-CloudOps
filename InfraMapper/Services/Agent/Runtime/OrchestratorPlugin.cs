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
    private readonly PlanStore _planStore;
    private readonly QuestionStore _questionStore;
    private readonly string _sessionId;

    public OrchestratorPlugin(
        SkAgentRunner runner,
        ChatCompletionAgent investigator,
        ChatCompletionAgent planner,
        PlannerTools plannerTools,
        ChatCompletionAgent critic,
        ChatCompletionAgent questioner,
        ChatCompletionAgent executor,
        ChatCompletionAgent reflector,
        PlanStore planStore,
        QuestionStore questionStore,
        string sessionId)
    {
        _runner = runner;
        _investigator = investigator;
        _planner = planner;
        _plannerTools = plannerTools;
        _critic = critic;
        _questioner = questioner;
        _executor = executor;
        _reflector = reflector;
        _planStore = planStore;
        _questionStore = questionStore;
        _sessionId = sessionId;
    }

    private string? GetAnswers() =>
        ClarificationAnswerFormatter.Format(_questionStore.GetAnsweredForSession(_sessionId));

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
        return _runner.RunAsync(_planner, PlannerAgent.BuildUserMessage(intent, investigator_summary, GetAnswers()), cancellationToken);
    }

    [KernelFunction("critique_plan")]
    [Description("Validate a deployment plan for correctness, naming rules, dependency ordering, region/SKU compatibility, and security.")]
    public Task<string> CritiquePlan(
        [Description("The plan_id to critique")] string plan_id,
        CancellationToken cancellationToken = default) =>
        _runner.RunAsync(_critic, CriticAgent.BuildUserMessage(plan_id, GetAnswers()), cancellationToken);

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
        [Description("The approved plan_id to execute. Optional — if omitted, the latest user-approved plan in this session is used.")] string? plan_id = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = ResolvePlanId(plan_id);
        if (resolved is null)
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                error = true,
                error_type = "missing_plan",
                message = "No approved plan found for this session. Ask the user to approve a plan first."
            });

        return await _runner.RunAsync(_executor, ExecutorAgent.BuildUserMessage(resolved, GetAnswers()), cancellationToken);
    }

    private string? ResolvePlanId(string? planId)
    {
        if (!string.IsNullOrWhiteSpace(planId) && Guid.TryParse(planId, out _))
            return planId;
        var latest = _planStore.GetLatestApprovedForSession(_sessionId);
        return latest?.ToString();
    }

    [KernelFunction("reflect_on_deployment")]
    [Description("Audit a completed deployment and record lessons for future planning. Call after every execute_plan.")]
    public Task<string> ReflectOnDeployment(
        [Description("Summary of what was deployed, what succeeded, and what failed")] string summary,
        CancellationToken cancellationToken = default) =>
        _runner.RunAsync(_reflector, ReflectorAgent.BuildUserMessage(summary), cancellationToken);
}
