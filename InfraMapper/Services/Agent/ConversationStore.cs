using System.Collections.Concurrent;
using System.Text.Json;
using InfraMapper.Services.Agent.Runtime;
using InfraMapper.Services.Agent.SubAgents;
using InfraMapper.Services.Agent.Tools;
using Microsoft.SemanticKernel.Agents;

namespace InfraMapper.Services.Agent;

public sealed class ConversationStore
{
    public sealed record SessionEntry(
        ChatCompletionAgent Agent,
        SkAgentSession Session,
        DateTimeOffset LastAccessed);

    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();

    // Service deps needed to build OrchestratorTools per session.
    private readonly AzureResourceService _resourceService;
    private readonly IArmDeploymentService _deploymentService;
    private readonly IApprovalService _approvalService;
    private readonly IResourceMutationApprovalService _mutationApprovals;
    private readonly IArmGenericResourceService _genericResources;
    private readonly PlanStore _planStore;
    private readonly QuestionStore _questionStore;
    private readonly SkAgentFactory _agentFactory;
    private readonly SkAgentRunner _runner;
    private readonly InvestigatorAgent _investigatorAgent;
    private readonly PlannerAgent _plannerAgent;
    private readonly CriticAgent _criticAgent;
    private readonly QuestionerAgent _questionerAgent;
    private readonly ExecutorAgent _executorAgent;
    private readonly ReflectorAgent _reflectorAgent;
    private readonly ILoggerFactory _loggerFactory;

    public ConversationStore(
        AzureResourceService resourceService,
        IArmDeploymentService deploymentService,
        IApprovalService approvalService,
        IResourceMutationApprovalService mutationApprovals,
        IArmGenericResourceService genericResources,
        PlanStore planStore,
        QuestionStore questionStore,
        SkAgentFactory agentFactory,
        SkAgentRunner runner,
        InvestigatorAgent investigatorAgent,
        PlannerAgent plannerAgent,
        CriticAgent criticAgent,
        QuestionerAgent questionerAgent,
        ExecutorAgent executorAgent,
        ReflectorAgent reflectorAgent,
        ILoggerFactory loggerFactory)
    {
        _resourceService = resourceService;
        _deploymentService = deploymentService;
        _approvalService = approvalService;
        _mutationApprovals = mutationApprovals;
        _genericResources = genericResources;
        _planStore = planStore;
        _questionStore = questionStore;
        _agentFactory = agentFactory;
        _runner = runner;
        _investigatorAgent = investigatorAgent;
        _plannerAgent = plannerAgent;
        _criticAgent = criticAgent;
        _questionerAgent = questionerAgent;
        _executorAgent = executorAgent;
        _reflectorAgent = reflectorAgent;
        _loggerFactory = loggerFactory;
    }

    public Task<SessionEntry> GetOrCreateAsync(string sessionId, string subscriptionId, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(sessionId, out var existing))
        {
            var refreshed = existing with { LastAccessed = DateTimeOffset.UtcNow };
            _sessions[sessionId] = refreshed;
            return Task.FromResult(refreshed);
        }

        var orcTools = new OrchestratorTools(
            _resourceService, _deploymentService, _approvalService,
            _mutationApprovals, _genericResources, _planStore,
            sessionId, _loggerFactory.CreateLogger<OrchestratorTools>());

        var orchestratorQuestioner = _questionerAgent.BuildForSession(sessionId, "orchestrator");
        var investigatorQuestioner = _questionerAgent.BuildForSession(sessionId, "investigator");
        var plannerQuestioner = _questionerAgent.BuildForSession(sessionId, "planner");
        var criticQuestioner = _questionerAgent.BuildForSession(sessionId, "critic");
        var executorQuestioner = _questionerAgent.BuildForSession(sessionId, "executor");
        var reflectorQuestioner = _questionerAgent.BuildForSession(sessionId, "reflector");

        var investigator = _investigatorAgent.Build(new OrchestratorPluginClarifier(_runner, investigatorQuestioner, "investigator"));
        var (planner, plannerTools) = _plannerAgent.BuildForSession(sessionId, new OrchestratorPluginClarifier(_runner, plannerQuestioner, "planner"));
        var critic = _criticAgent.BuildForSession(clarificationPlugin: new OrchestratorPluginClarifier(_runner, criticQuestioner, "critic"));
        var executor = _executorAgent.BuildForSession(sessionId, subscriptionId, new OrchestratorPluginClarifier(_runner, executorQuestioner, "executor"));
        var reflector = _reflectorAgent.Build(new OrchestratorPluginClarifier(_runner, reflectorQuestioner, "reflector"));

        var orchestratorPlugin = new OrchestratorPlugin(
            _runner,
            investigator,
            planner,
            plannerTools,
            critic,
            orchestratorQuestioner,
            executor,
            reflector);

        var agent = _agentFactory.Create(
            "orchestrator",
            BuildSystemPrompt(subscriptionId),
            (orchestratorPlugin, "orchestrator"),
            (orcTools, "azure"));

        var session = new SkAgentSession();
        var entry = new SessionEntry(agent, session, DateTimeOffset.UtcNow);

        // Use AddOrUpdate to handle the rare case of concurrent first requests.
        var stored = _sessions.AddOrUpdate(sessionId, entry, (_, existing2) =>
        {
            // Another thread won the race; keep theirs but update accessed time.
            return existing2 with { LastAccessed = DateTimeOffset.UtcNow };
        });

        return Task.FromResult(stored);
    }

    public void SetPendingApproval(string sessionId, Guid planId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        entry.Session.SetValue(
            "pending_approval",
            $"The plan with id {planId} has been approved by the user. Call execute_plan with plan_id \"{planId}\" now, then call reflect_on_deployment after execution completes.");
    }

    public string? ConsumePendingApproval(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (!entry.Session.TryGetValue<string>("pending_approval", out var msg)) return null;
        entry.Session.TryRemoveValue("pending_approval");
        return msg;
    }

    public void SetPendingQuestionAnswer(string sessionId, Guid questionId, string answer)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        entry.Session.TryGetValue<string>("pending_question_answer", out var existing);
        var answerContext = _questionStore.GetAnswerContext(questionId);
        var next = answerContext is null
            ? $"The user answered clarification question {questionId}: {answer}."
            : BuildClarificationResumeMessage(answerContext);
        entry.Session.SetValue(
            "pending_question_answer",
            string.IsNullOrWhiteSpace(existing)
                ? $"{next} Continue planning with this answer. If the answer changes plan constraints, call plan_deployment again and critique the revised plan before presenting it."
                : $"{existing}\n{next}");
    }

    private static string BuildClarificationResumeMessage(ClarifyingQuestionAnswerContext answer)
    {
        var payload = JsonSerializer.Serialize(new
        {
            question_id = answer.QuestionId,
            originating_agent = answer.OriginatingAgent,
            title = answer.Title,
            prompt = answer.Prompt,
            selected_value = answer.SelectedValue,
            selected_label = answer.SelectedLabel,
            selected_description = answer.SelectedDescription,
            default_value = answer.DefaultValue,
            category = answer.Category,
            confirmation_scope = answer.ConfirmationScope,
            is_scope_confirmation = answer.IsScopeConfirmation,
            answered_at = answer.AnsweredAt
        }, OrchestratorTools.SnakeCaseOpts);

        return $"""
            The user answered a structured clarification questionnaire.
            Clarification answer context:
            {payload}
            Treat this answer as first-class plan context. If is_scope_confirmation is true,
            it confirms the matching destructive scope for the current plan unless the plan expands scope.
            Before asking another requires_user_choice question, compare the requested choice against
            this existing clarification context.
            """;
    }

    public string? ConsumePendingQuestionAnswer(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (!entry.Session.TryGetValue<string>("pending_question_answer", out var msg)) return null;
        entry.Session.TryRemoveValue("pending_question_answer");
        return msg;
    }

    public void Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);

    public void Evict(TimeSpan maxIdle)
    {
        var cutoff = DateTimeOffset.UtcNow - maxIdle;
        foreach (var key in _sessions.Keys)
            if (_sessions.TryGetValue(key, out var entry) && entry.LastAccessed < cutoff)
                _sessions.TryRemove(key, out _);
    }

    private static string BuildSystemPrompt(string subscriptionId) => $"""
        You are InfraMapper Agent, an AI assistant that manages Azure infrastructure.

        The user's Azure subscription ID is: {subscriptionId}
        Use this subscription ID for all operations unless the user explicitly specifies a different one.
        Do NOT ask the user for their subscription ID.

        Rules:
        - INVESTIGATE: call investigate_infrastructure(focus, subscription_id?) freely before planning.
          The Investigator discovers existing resources and returns a focused summary.
        - CHECK STATUS: call get_deployment_status for deployment status checks.
        - ASK: call ask_clarifying_question only when planning is blocked by ambiguity,
          planner needs a preference, or critic feedback requires a human choice.
          Do not ask for discoverable Azure facts; use investigate_infrastructure first.
          Ask before plan_deployment if the user intent is missing a required deployment choice
          (for example SKU/tier, destructive scope, region when not inferable, or mutually exclusive architecture).
          Ask after critique_plan only when the critic's feedback cannot be resolved safely without user intent.

        ── QUESTIONNAIRE PROTOCOL ──────────────────────────────────────────────
        All user-facing questions MUST be created with ask_clarifying_question so the UI
        renders a questionnaire. Do NOT ask questions in plain prose. If your response
        would ask the user to confirm, choose, provide exclusions, explain business intent,
        or answer anything else before work can continue, call ask_clarifying_question
        instead of replying with the question text.
        All sub-agents also have access to ask_clarifying_question. If a sub-agent returns
        question JSON or emits a questionnaire, STOP and wait for the user's answer before
        continuing the current plan/execution path.

        Broad destructive requests require a questionnaire before planning unless the user
        already gave explicit scope, exclusions, and confirmation. Examples include:
        delete all resource groups, delete a subscription's resources, delete production
        workloads, or any request whose scope may remove many resources.
        Structured clarification answers are first-class context. Scope-level confirmation
        applies to every matching destructive operation in the current plan unless a later
        plan expands the destructive scope. Do not ask for per-resource typed confirmation
        when an existing scope-level answer already covers that operation.

        ── BLAST RADIUS ASSESSMENT ──────────────────────────────────────────────
        Before acting on any mutating request, assess blast radius:

          LOW — single resource, non-cascading, easily reversible.
                Examples: update a tag, change a SKU on a non-prod resource,
                          delete a single named resource (not a resource group).

          MEDIUM — new single resource creation, or delete of a single named resource
                   where the scope is clear and contained.

          HIGH — anything that could affect many resources or is hard to reverse:
                 delete a resource group, deploy an ARM template, create/modify
                 networking (VNets, NSGs), modify production workloads, multi-resource ops.

        ── APPROVAL PROTOCOL (READ THIS CAREFULLY) ──────────────────────────────
        Plans require explicit human approval before execution. The UI shows an Approve button.

        PHASE A — PLANNING (current turn):
          After you produce a plan (and critique it if HIGH), you MUST:
          • Output a concise summary of the plan to the user.
          • STOP. Do NOT call execute_plan. Do NOT call any write tools.
          • The user will click Approve in the UI, then send a follow-up message.

        PHASE B — EXECUTION (next turn, triggered by approval signal):
          You will receive a message containing:
            "The plan with id <plan_id> has been approved by the user. Call execute_plan with plan_id \"<plan_id>\" now, then call reflect_on_deployment after execution completes."
          This is your ONLY signal to call execute_plan. Do not call execute_plan without it.
          If execute_plan returns error_type:"plan_not_approved", do NOT retry —
          the user has not approved yet; tell them to click the Approve button.

        ── EXECUTION PATHS BY BLAST RADIUS ─────────────────────────────────────

          LOW path:
          PHASE A: Call plan_deployment(intent). Output plan summary. STOP.
          PHASE B: On approval signal → call execute_plan(plan_id) → call reflect_on_deployment.

          MEDIUM path:
          PHASE A: Call plan_deployment(intent, investigator_summary?). Output plan summary. STOP.
          PHASE B: On approval signal → call execute_plan(plan_id).
                   If needs_replan:true, call plan_deployment once, then execute again.
                   Call reflect_on_deployment.

          HIGH path:
          PHASE A:
          1. If intent needs confirmation, scope, exclusions, or a business reason, call
             ask_clarifying_question. STOP until the user answers through the questionnaire.
          2. Optionally call investigate_infrastructure if context is needed.
          3. Call plan_deployment(intent, investigator_summary?). Include any structured
             clarification answer context in the intent so Planner documents confirmation evidence.
          4. Call critique_plan(plan_id).
             If approved:false, call plan_deployment again with feedback. Repeat at most ONCE more (2 total plan calls).
             If feedback includes requires_user_choice, first compare it with existing structured
             clarification context. If already covered, re-plan/re-critique with the documented
             confirmation evidence. If not covered and a user choice can unblock the plan, call
             ask_clarifying_question; otherwise output the rejection reason.
          5. Once approved:true, output a concise plan summary. STOP.
          PHASE B: On approval signal → call execute_plan(plan_id).
                   If needs_replan:true, re-plan once, critique once, then execute.
                   Call reflect_on_deployment.

        General rules:
        - Be concise. After completing an operation, give a brief summary of what was done.

        Formatting rules:
        - Do NOT use emojis.
        - When writing a markdown table, always put a blank line before and after it, and put each row on its own line.
        - Use bullet points or numbered lists rather than inline-concatenated items.
        """;
}
