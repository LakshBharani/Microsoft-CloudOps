using System.Collections.Concurrent;
using Anthropic;
using InfraMapper.Services.Agent.SubAgents;
using InfraMapper.Services.Agent.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace InfraMapper.Services.Agent;

/// <summary>
/// Stores per-session (AIAgent, AgentSession) pairs so conversation history is
/// maintained across multiple HTTP requests. One entry per sessionId.
/// </summary>
public sealed class ConversationStore
{
    public sealed record SessionEntry(
        AIAgent Agent,
        AgentSession Session,
        DateTimeOffset LastAccessed);

    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();

    // Service deps needed to build OrchestratorTools per session.
    private readonly AzureResourceService _resourceService;
    private readonly IArmDeploymentService _deploymentService;
    private readonly IApprovalService _approvalService;
    private readonly IResourceMutationApprovalService _mutationApprovals;
    private readonly IArmGenericResourceService _genericResources;
    private readonly PlanStore _planStore;
    private readonly IAnthropicClient _anthropicClient;
    private readonly InvestigatorAgent _investigatorAgent;
    private readonly PlannerAgent _plannerAgent;
    private readonly CriticAgent _criticAgent;
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
        IAnthropicClient anthropicClient,
        InvestigatorAgent investigatorAgent,
        PlannerAgent plannerAgent,
        CriticAgent criticAgent,
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
        _executorAgent = executorAgent;
        _reflectorAgent = reflectorAgent;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Returns the existing session entry for <paramref name="sessionId"/>, or creates a new one
    /// using <paramref name="subscriptionId"/> to build the system prompt and tool instances.
    /// </summary>
    public async Task<SessionEntry> GetOrCreateAsync(string sessionId, string subscriptionId, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(sessionId, out var existing))
        {
            var refreshed = existing with { LastAccessed = DateTimeOffset.UtcNow };
            _sessions[sessionId] = refreshed;
            return refreshed;
        }

        var tools = new OrchestratorTools(
            _resourceService, _deploymentService, _approvalService,
            _mutationApprovals, _genericResources, _planStore,
            sessionId, _loggerFactory.CreateLogger<OrchestratorTools>());

        // Build sub-agents for this session and get their agent-tool functions.
        var (_, investigateFn)    = _investigatorAgent.Build();
        var (_, planDeploymentFn) = _plannerAgent.BuildForSession(sessionId);
        var (_, critiquePlanFn)   = _criticAgent.BuildForSession();
        var (_, executePlanFn)    = _executorAgent.BuildForSession(sessionId);
        var (_, reflectFn)        = _reflectorAgent.Build();

        var aiTools = BuildAiTools(tools, investigateFn, planDeploymentFn, critiquePlanFn, executePlanFn, reflectFn);
        var model = AgentRegistry.GetModel("orchestrator");
        var agent = _anthropicClient.AsAIAgent(
            model: model,
            instructions: BuildSystemPrompt(subscriptionId),
            name: "InfraMapperOrchestrator",
            description: "Manages Azure cloud infrastructure on behalf of the user.",
            tools: aiTools);

        var session = await agent.CreateSessionAsync(ct);
        var entry = new SessionEntry(agent, session, DateTimeOffset.UtcNow);

        // Use AddOrUpdate to handle the rare case of concurrent first requests.
        return _sessions.AddOrUpdate(sessionId, entry, (_, existing2) =>
        {
            // Another thread won the race; keep theirs but update accessed time.
            return existing2 with { LastAccessed = DateTimeOffset.UtcNow };
        });
    }

    /// <summary>Stores a pending "plan approved" message in the session StateBag for pickup on the next stream call.</summary>
    public void SetPendingApproval(string sessionId, Guid planId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        entry.Session.StateBag.SetValue(
            "pending_approval",
            $"The plan with id {planId} has been approved. Please proceed with execution.");
    }

    /// <summary>Removes and returns a pending approval message, if any.</summary>
    public string? ConsumePendingApproval(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (!entry.Session.StateBag.TryGetValue<string>("pending_approval", out var msg)) return null;
        entry.Session.StateBag.TryRemoveValue("pending_approval");
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

    private static IList<AITool> BuildAiTools(
        OrchestratorTools tools,
        AIFunction investigateFn,
        AIFunction planDeploymentFn,
        AIFunction critiquePlanFn,
        AIFunction executePlanFn,
        AIFunction reflectFn)
    {
        var so = OrchestratorTools.SnakeCaseOpts;
        return
        [
            // Investigator: resource discovery with self-reflection.
            investigateFn,
            // Planner: draft → get_lessons → record_critique → create_plan.
            planDeploymentFn,
            // Critic: validate plan; approved/rejected with actionable feedback.
            critiquePlanFn,
            // Executor: applies approved plans; returns needs_replan:true on failures.
            executePlanFn,
            // Reflector: post-deployment audit; writes lessons to persistent store.
            reflectFn,
            // Direct read: deployment status check for ad-hoc queries.
            AIFunctionFactory.Create(tools.GetDeploymentStatusAsync,
                new AIFunctionFactoryOptions { Name = "get_deployment_status", SerializerOptions = so }),
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
        - PLAN AND CRITIQUE LOOP: when you need to deploy or modify Azure resources:
          1. Call plan_deployment(intent, investigator_summary?) — the Planner drafts, self-critiques, and creates a plan.
          2. IMMEDIATELY after plan_deployment, ALWAYS call critique_plan(plan_id) — the Critic validates it.
          3. If critique_plan returns approved:false, call plan_deployment again with the feedback (max 3 cycles).
          4. Once critique_plan returns approved:true, inform the user the plan is ready for review.
          5. Do NOT call write tools until you receive confirmation the plan was approved by the user.
          6. Once user approves, call execute_plan(plan_id) — the Executor applies the plan to Azure.
          7. If execute_plan returns needs_replan:true, call plan_deployment again with the error (max 2 retries),
             then critique_plan, then execute_plan again.
          8. After execute_plan finishes (success or failure), ALWAYS call reflect_on_deployment with a
             summary of what happened. This builds institutional memory for future deployments.
        - Be concise. After completing an operation, give a brief summary of what was done.

        Formatting rules:
        - Do NOT use emojis.
        - When writing a markdown table, always put a blank line before and after it, and put each row on its own line.
        - Use bullet points or numbered lists rather than inline-concatenated items.
        """;
}
