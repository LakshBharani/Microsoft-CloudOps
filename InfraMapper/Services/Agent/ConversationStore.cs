using System.Collections.Concurrent;
using Anthropic;
using InfraMapper.Services.Agent.AgentFramework;
using InfraMapper.Services.Agent.SubAgents;
using InfraMapper.Services.Agent.Tools;

namespace InfraMapper.Services.Agent;

/// <summary>
/// Stores per-session (AnthropicAgent, AnthropicAgentSession) pairs so conversation history is
/// maintained across multiple HTTP requests. One entry per sessionId.
/// </summary>
public sealed class ConversationStore
{
    public sealed record SessionEntry(
        AnthropicAgent Agent,
        AnthropicAgentSession Session,
        DateTimeOffset LastAccessed);

    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();

    // Service deps needed to build OrchestratorTools per session.
    private readonly AzureResourceService _resourceService;
    private readonly IArmDeploymentService _deploymentService;
    private readonly IApprovalService _approvalService;
    private readonly IResourceMutationApprovalService _mutationApprovals;
    private readonly IArmGenericResourceService _genericResources;
    private readonly PlanStore _planStore;
    private readonly AnthropicClient _anthropicClient;
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
        AnthropicClient anthropicClient,
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
        _anthropicClient = anthropicClient;
        _investigatorAgent = investigatorAgent;
        _plannerAgent = plannerAgent;
        _criticAgent = criticAgent;
        _questionerAgent = questionerAgent;
        _executorAgent = executorAgent;
        _reflectorAgent = reflectorAgent;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Returns the existing session entry for <paramref name="sessionId"/>, or creates a new one
    /// using <paramref name="subscriptionId"/> to build the system prompt and tool instances.
    /// </summary>
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

        // Build sub-agents for this session and get their agent-tool functions.
        var (_, investigateFn)    = _investigatorAgent.Build();
        var (_, planDeploymentFn) = _plannerAgent.BuildForSession(sessionId);
        var (_, critiquePlanFn)   = _criticAgent.BuildForSession();
        var (_, questionFn)       = _questionerAgent.BuildForSession(sessionId);
        var (_, executePlanFn)    = _executorAgent.BuildForSession(sessionId, subscriptionId);
        var (_, reflectFn)        = _reflectorAgent.Build();

        var agentTools = BuildAgentTools(orcTools, investigateFn, planDeploymentFn, critiquePlanFn, questionFn, executePlanFn, reflectFn);
        var model = AgentRegistry.GetModel("orchestrator");

        var agent = new AnthropicAgent(
            _anthropicClient,
            model,
            BuildSystemPrompt(subscriptionId),
            agentTools);

        var session = new AnthropicAgentSession();
        var entry = new SessionEntry(agent, session, DateTimeOffset.UtcNow);

        // Use AddOrUpdate to handle the rare case of concurrent first requests.
        var stored = _sessions.AddOrUpdate(sessionId, entry, (_, existing2) =>
        {
            // Another thread won the race; keep theirs but update accessed time.
            return existing2 with { LastAccessed = DateTimeOffset.UtcNow };
        });

        return Task.FromResult(stored);
    }

    /// <summary>Stores a pending "plan approved" message in the session StateBag for pickup on the next stream call.</summary>
    public void SetPendingApproval(string sessionId, Guid planId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        entry.Session.SetValue(
            "pending_approval",
            $"The plan with id {planId} has been approved by the user. Call execute_plan with plan_id \"{planId}\" now, then call reflect_on_deployment after execution completes.");
    }

    /// <summary>Removes and returns a pending approval message, if any.</summary>
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
        var next = $"The user answered clarification question {questionId}: {answer}.";
        entry.Session.SetValue(
            "pending_question_answer",
            string.IsNullOrWhiteSpace(existing)
                ? $"{next} Continue planning with this answer. If the answer changes plan constraints, call plan_deployment again and critique the revised plan before presenting it."
                : $"{existing}\n{next}");
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

    private static IList<AgentTool> BuildAgentTools(
        OrchestratorTools tools,
        AgentTool investigateFn,
        AgentTool planDeploymentFn,
        AgentTool critiquePlanFn,
        AgentTool questionFn,
        AgentTool executePlanFn,
        AgentTool reflectFn)
    {
        return
        [
            // Investigator: resource discovery with self-reflection.
            investigateFn,
            // Planner: draft → get_lessons → record_critique → create_plan.
            planDeploymentFn,
            // Critic: validate plan; approved/rejected with actionable feedback.
            critiquePlanFn,
            // Questioner: ask user when planning is blocked by ambiguity.
            questionFn,
            // Executor: applies approved plans; returns needs_replan:true on failures.
            executePlanFn,
            // Reflector: post-deployment audit; writes lessons to persistent store.
            reflectFn,
            // Direct read: deployment status check for ad-hoc queries.
            AgentToolFactory.Create(tools.GetDeploymentStatusAsync,
                "get_deployment_status",
                "Get the status of an Azure deployment by subscription, deployment name, and optional resource group."),
        ];
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
          1. Optionally call investigate_infrastructure if context is needed.
          2. Call plan_deployment(intent, investigator_summary?).
          3. Call critique_plan(plan_id).
             If approved:false, call plan_deployment again with feedback. Repeat at most ONCE more (2 total plan calls).
             If still approved:false after 2 plan calls, call ask_clarifying_question with the rejection reason
             if a user choice can unblock the plan; otherwise output the rejection reason.
          4. Once approved:true, output a concise plan summary. STOP.
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
