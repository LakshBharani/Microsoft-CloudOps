using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using InfraMapper.Models.Agent;
using InfraMapper.Services.Agent.Runtime;
using InfraMapper.Services.Agent.State;
using InfraMapper.Services.Agent.Tools;
using Microsoft.SemanticKernel.Agents;

namespace InfraMapper.Services.Agent;

public sealed class AgentService
{
    private readonly ConversationStore _store;
    private readonly PlanStore _planStore;
    private readonly QuestionStore _questionStore;
    private readonly SkAgentRunner _runner;
    private readonly AzureResourceService _resourceService;
    private readonly ArmExistenceProbe _existenceProbe;

    public AgentService(
        ConversationStore store,
        PlanStore planStore,
        QuestionStore questionStore,
        SkAgentRunner runner,
        AzureResourceService resourceService,
        ArmExistenceProbe existenceProbe)
    {
        _store = store;
        _planStore = planStore;
        _questionStore = questionStore;
        _runner = runner;
        _resourceService = resourceService;
        _existenceProbe = existenceProbe;
    }

    public async Task<AgentChatResponse> ChatAsync(AgentChatRequest request, CancellationToken ct)
    {
        string? reply = null;
        string? sessionId = null;

        await foreach (var evt in StreamAsync(request, ct))
        {
            using var doc = JsonDocument.Parse(evt);
            var root = doc.RootElement;
            if (root.GetProperty("type").GetString() == "reply")
            {
                var data = root.GetProperty("data");
                reply = data.GetProperty("content").GetString();
                sessionId = data.GetProperty("session_id").GetString();
            }
        }

        return new AgentChatResponse { Reply = reply ?? "", SessionId = sessionId ?? request.SessionId ?? "" };
    }

    public async IAsyncEnumerable<string> StreamAsync(
        AgentChatRequest request,
        [EnumeratorCancellation] CancellationToken ct,
        bool autoApprovePlan = false)
    {
        var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? Guid.NewGuid().ToString()
            : request.SessionId;

        // --- get or create session ---
        ConversationStore.SessionEntry? entry = null;
        string? initError = null;
        try { entry = await _store.GetOrCreateAsync(sessionId, request.SubscriptionId, ct); }
        catch (Exception ex) { initError = ex.Message; }

        if (initError is not null)
        {
            yield return SseEventTranslator.Evt("error", new { message = initError, session_id = sessionId });
            yield break;
        }

        _store.IngestIntent(sessionId, request.SubscriptionId, request.Message);

        // Inject approval message if the user approved a plan while the stream was closed.
        var pendingApproval = _store.ConsumePendingApproval(sessionId);
        var pendingQuestionAnswer = _store.ConsumePendingQuestionAnswer(sessionId);
        var pending = string.Join("\n", new[] { pendingApproval, pendingQuestionAnswer }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var effectiveMessage = !string.IsNullOrWhiteSpace(pending)
            ? $"{pending}\n{request.Message}"
            : request.Message;

        // --- build stream ---
        IAsyncEnumerable<string>? events = null;
        string? streamError = null;
        try
        {
            var agentStream = pendingApproval is not null && TryExtractApprovedPlanId(pendingApproval, out var approvedPlanId)
                ? RunExecutionRecoveryAsync(entry!, sessionId, approvedPlanId, request.SubscriptionId, ct)
                : ShouldRunDeterministicIntentPlanning(sessionId, pendingApproval, pendingQuestionAnswer)
                    ? RunDeterministicPlanningAsync(entry!.Orchestrator, sessionId, effectiveMessage, request.SubscriptionId, ct)
                    : RunWithProgressRetryAsync(entry!, effectiveMessage, request.SubscriptionId, ct);
            var translator = new SseEventTranslator(sessionId, _planStore, _questionStore, autoApprovePlan);
            events = translator.TranslateAsync(agentStream, ct);
        }
        catch (Exception ex) { streamError = ex.Message; }

        if (streamError is not null)
        {
            yield return SseEventTranslator.Evt("error", new { message = streamError, session_id = sessionId });
            yield break;
        }

        await foreach (var evt in events!.WithCancellation(ct))
            yield return evt;
    }

    private async IAsyncEnumerable<AgentStreamEvent> RunExecutionRecoveryAsync(
        ConversationStore.SessionEntry entry,
        string sessionId,
        string planId,
        string subscriptionId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var execution = await RunDirectToolAsync(
            "execute_plan",
            () => entry.Orchestrator.ExecutePlan(planId, ct));
        foreach (var evt in execution.Events)
            yield return evt;

        if (execution.Success && !LooksLikeStructuredExecutionResult(execution.Result))
        {
            execution = execution with
            {
                Success = false,
                Result = JsonSerializer.Serialize(new
                {
                    error = true,
                    error_type = "executor_no_result",
                    needs_replan = true,
                    message = "Executor finished without returning a structured deployment result. Treating this as a failed execution so Planner can revise the plan instead of stopping."
                }, OrchestratorTools.SnakeCaseOpts)
            };
        }

        var needsRecovery = NeedsRecovery(execution.Result, out var errorType, out var message);

        if (execution.Success && !needsRecovery)
        {
            var reflection = await RunDirectToolAsync(
                "reflect_on_deployment",
                () => entry.Orchestrator.ReflectOnDeployment(execution.Result, ct));
            foreach (var evt in reflection.Events)
                yield return evt;
            yield break;
        }

        if (!needsRecovery)
        {
            if (execution.Success)
                yield break;

            errorType = "execution_failed";
            message = execution.Result;
        }

        var taskState = _store.GetTaskState(sessionId);
        var planData = Guid.TryParse(planId, out var planGuid) ? _planStore.GetPlanData(planGuid) : null;
        var templateHash = ExtractTemplateHashFromPlanData(planData);
        if (taskState is not null)
        {
            taskState.AddFailure(new ExecutionFailureSnapshot(
                ErrorType: errorType,
                Message: message,
                TemplateHash: templateHash,
                PlanId: planGuid,
                Timestamp: DateTimeOffset.UtcNow));

            if (taskState.HasRecentDuplicateFailure())
            {
                var pauseQuestion = await RunDirectToolAsync(
                    "ask_clarifying_question",
                    () => entry.Orchestrator.AskClarifyingQuestion(
                        $"""
                        Same Azure failure repeated for plan {planId}. Replanning with the same template will
                        not help. Pause and ask the user for guidance before spending more Planner calls.
                        Last error_type: {errorType}
                        Last error: {message}
                        """,
                        "Pause execution and request human guidance",
                        "general",
                        "execution_recovery",
                        "executor",
                        ct));
                foreach (var evt in pauseQuestion.Events)
                    yield return evt;
                yield break;
            }
        }

        var investigation = await RunDirectToolAsync(
            "investigate_infrastructure",
            () => entry.Orchestrator.InvestigateInfrastructure(
                BuildRecoveryInvestigationFocus(planId, errorType, message),
                subscriptionId,
                ct));
        foreach (var evt in investigation.Events)
            yield return evt;

        if (string.Equals(errorType, "quota", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("quota", StringComparison.OrdinalIgnoreCase))
        {
            var question = await RunDirectToolAsync(
                "ask_clarifying_question",
                () => entry.Orchestrator.AskClarifyingQuestion(
                    $"""
                    Execution failed because Azure quota blocked the approved plan.
                    Plan id: {planId}
                    Error: {message}
                    Investigator confirmation: {investigation.Result}

                    Propose a user choice that lets InfraMapper recover and retry with a revised plan.
                    Include an option to revise the App Service Plan to a lower tier that avoids Standard VM quota,
                    such as Free F1 or Shared D1 when appropriate, and an option to pause and request quota increase.
                    After the user chooses, continue by generating a new plan, showing it for approval, and retrying execution after approval.
                    """,
                    "Revise plan to avoid Standard VM quota",
                    "general",
                    "execution_recovery",
                    "executor",
                    ct));
            foreach (var evt in question.Events)
                yield return evt;
            yield break;
        }

        var revisedPlan = await RunDirectToolAsync(
            "plan_deployment",
            () => entry.Orchestrator.PlanDeployment(
                BuildRecoveryPlanningIntent(planId, errorType, message, investigation.Success, investigation.Result),
                investigation.Success ? investigation.Result : null,
                ct));
        foreach (var evt in revisedPlan.Events)
            yield return evt;

        if (revisedPlan.Success && TryExtractPlanId(revisedPlan.Result, out var revisedPlanId))
        {
            var critique = await RunDirectToolAsync(
                "critique_plan",
                () => entry.Orchestrator.CritiquePlan(revisedPlanId, ct));
            foreach (var evt in critique.Events)
                yield return evt;
        }
    }

    private async IAsyncEnumerable<AgentStreamEvent> RunWithProgressRetryAsync(
        ConversationStore.SessionEntry entry,
        string message,
        string subscriptionId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var first = await RunBufferedUntilToolCallAsync(entry.Agent, message, entry.Session, ct);
        if (first.SawToolCall)
        {
            foreach (var evt in first.Events)
                yield return evt;
            yield break;
        }

        if (!ShouldRetryForMissingProgress(message, first.FinalText))
        {
            foreach (var evt in first.Events)
                yield return evt;
            yield break;
        }

        var retryId = $"orchestrator_retry_{Guid.NewGuid():N}";
        yield return new AgentStreamEvent.Activity(
            "start",
            retryId,
            null,
            "agent",
            "orchestrator",
            null,
            AgentRegistry.GetModel("orchestrator"),
            "running",
            "Continuing orchestration",
            Message: "First response did not call any tools; retrying with required tool use.");

        var retry = await RunBufferedUntilToolCallAsync(
            entry.Agent,
            BuildRequiredProgressMessage(message, first.FinalText),
            entry.Session,
            ct);

        if (retry.SawToolCall)
        {
            foreach (var evt in retry.Events)
                yield return evt;
            yield return new AgentStreamEvent.Activity(
                "end",
                retryId,
                null,
                "agent",
                "orchestrator",
                null,
                AgentRegistry.GetModel("orchestrator"),
                "success",
                "Orchestration continued",
                Message: "Tool-driven workflow resumed.");
            yield break;
        }

        yield return new AgentStreamEvent.Activity(
            "end",
            retryId,
            null,
            "agent",
            "orchestrator",
            null,
            AgentRegistry.GetModel("orchestrator"),
            "success",
            "Switching to deterministic workflow",
            Message: "Model returned text again; server is running investigation and planning directly.");

        await foreach (var evt in RunDeterministicPlanningAsync(entry.Orchestrator, entry.TaskState.SessionId, message, subscriptionId, ct))
            yield return evt;
    }

    private async Task<BufferedRun> RunBufferedUntilToolCallAsync(
        ChatCompletionAgent agent,
        string message,
        SkAgentSession session,
        CancellationToken ct)
    {
        var events = new List<AgentStreamEvent>();
        var sawToolCall = false;
        string finalText = "";

        await foreach (var evt in _runner.RunStreamingAsync(agent, message, session, ct))
        {
            if (evt is AgentStreamEvent.ToolCall)
                sawToolCall = true;

            if (evt is AgentStreamEvent.Done done)
                finalText = done.Text;
            events.Add(evt);
        }

        return new BufferedRun(sawToolCall, finalText, events);
    }

    private sealed record BufferedRun(bool SawToolCall, string FinalText, List<AgentStreamEvent> Events);

    private async IAsyncEnumerable<AgentStreamEvent> RunDeterministicPlanningAsync(
        OrchestratorPlugin orchestrator,
        string sessionId,
        string intent,
        string subscriptionId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var taskState = _store.GetTaskState(sessionId);
        DirectToolRun investigation;
        if (taskState is not null && !string.IsNullOrWhiteSpace(taskState.ResourceGroup))
        {
            var fastCallId = $"investigate_infrastructure_fast_{Guid.NewGuid():N}";
            yield return new AgentStreamEvent.ToolCall("investigate_infrastructure", fastCallId);
            investigation = await RunFastInvestigationAsync(subscriptionId, taskState, fastCallId, ct);
            Console.WriteLine($"[Planning] fast_investigation_done success={investigation.Success}");
            yield return new AgentStreamEvent.ToolResult(
                "investigate_infrastructure",
                fastCallId,
                investigation.Success,
                investigation.Result);
        }
        else
        {
            investigation = await RunDirectToolAsync(
                "investigate_infrastructure",
                () => orchestrator.InvestigateInfrastructure(intent, subscriptionId, ct));
            foreach (var evt in investigation.Events)
                yield return evt;
        }
        if (!investigation.Success)
            yield break;
        if (taskState is not null) taskState.InvestigationSummary = investigation.Result;

        Console.WriteLine($"[Planning] calling_planner intent_len={intent.Length}");
        var plan = await RunDirectToolAsync(
            "plan_deployment",
            () => orchestrator.PlanDeployment(intent, investigation.Result, ct));
        Console.WriteLine($"[Planning] planner_done success={plan.Success} result_len={plan.Result.Length} result_preview={PreviewForLog(plan.Result)}");
        foreach (var evt in plan.Events)
            yield return evt;
        if (!plan.Success || TryExtractQuestionId(plan.Result, out _))
            yield break;

        if (!TryExtractPlanId(plan.Result, out var planId) &&
            !TryExtractPlanIdFromEvents(plan.Events, out planId))
        {
            Console.WriteLine($"[Planning] plan_id_extract_failed result_preview={PreviewForLog(plan.Result)}");
            var deterministicPlan = TryCreateDeterministicPlan(intent);
            if (deterministicPlan is null)
                yield break;

            foreach (var evt in deterministicPlan.Events)
                yield return evt;
            planId = deterministicPlan.PlanId;
        }

        var critique = await RunDirectToolAsync(
            "critique_plan",
            () => orchestrator.CritiquePlan(planId, ct));
        foreach (var evt in critique.Events)
            yield return evt;

        if (critique.Success &&
            TryExtractCriticVerdict(critique.Result, out var approved, out var feedback) &&
            approved == false)
        {
            Console.WriteLine($"[Planning] critic_rejected plan_id={planId} feedback={PreviewForLog(feedback)}");
            if (!IsBlockingCriticFeedback(feedback))
            {
                Console.WriteLine($"[Planning] critic_feedback_advisory plan_id={planId}");
                yield break;
            }

            var revisionIntent = BuildCriticRevisionIntent(intent, planId, feedback);
            var revisedPlan = await RunDirectToolAsync(
                "plan_deployment",
                () => orchestrator.PlanDeployment(revisionIntent, investigation.Result, ct));
            Console.WriteLine($"[Planning] revised_planner_done success={revisedPlan.Success} result_len={revisedPlan.Result.Length} result_preview={PreviewForLog(revisedPlan.Result)}");
            foreach (var evt in revisedPlan.Events)
                yield return evt;

            if (!revisedPlan.Success ||
                TryExtractQuestionId(revisedPlan.Result, out _) ||
                (!TryExtractPlanId(revisedPlan.Result, out var revisedPlanId) &&
                 !TryExtractPlanIdFromEvents(revisedPlan.Events, out revisedPlanId)))
                yield break;

            var revisedCritique = await RunDirectToolAsync(
                "critique_plan",
                () => orchestrator.CritiquePlan(revisedPlanId, ct));
            foreach (var evt in revisedCritique.Events)
                yield return evt;

            if (revisedCritique.Success &&
                TryExtractCriticVerdict(revisedCritique.Result, out var revisedApproved, out var revisedFeedback) &&
                revisedApproved == false)
            {
                Console.WriteLine($"[Planning] revised_critic_rejected plan_id={revisedPlanId} feedback={PreviewForLog(revisedFeedback)}");
                if (!IsBlockingCriticFeedback(revisedFeedback))
                {
                    Console.WriteLine($"[Planning] revised_critic_feedback_advisory plan_id={revisedPlanId}");
                    yield break;
                }

                yield return new AgentStreamEvent.Done($"Critic rejected the revised plan: {revisedFeedback}");
            }
        }
    }

    private DeterministicPlanRun? TryCreateDeterministicPlan(string intent)
    {
        var operations = ParseDiffOperations(intent);
        if (operations.Length == 0)
            return null;

        var planDataEl = JsonSerializer.SerializeToElement(new
        {
            title = "Apply infrastructure changes",
            operations,
            risk_level = operations.Length > 1 || operations.Any(IsHighRiskOperation) ? "High" : "Medium",
            estimated_cost_note = "May affect Azure spend if SKU, capacity, hosting plan tier, or resource kind changes."
        }, OrchestratorTools.SnakeCaseOpts);

        var planId = _planStore.CreatePlan(ExtractSessionIdFromIntent(intent), planDataEl);
        var result = JsonSerializer.Serialize(new
        {
            plan_id = planId.ToString(),
            status = "awaiting_user_approval",
            title = "Apply infrastructure changes",
            operations,
            risk_level = operations.Length > 1 || operations.Any(IsHighRiskOperation) ? "High" : "Medium",
            estimated_cost_note = "May affect Azure spend if SKU, capacity, hosting plan tier, or resource kind changes."
        }, OrchestratorTools.SnakeCaseOpts);

        var callId = $"plan_deployment_deterministic_{Guid.NewGuid():N}";
        return new DeterministicPlanRun(planId.ToString(), new List<AgentStreamEvent>
        {
            new AgentStreamEvent.ToolCall("plan_deployment", callId),
            new AgentStreamEvent.ToolResult("plan_deployment", callId, true, result)
        });
    }

    private sealed record DeterministicPlanRun(string PlanId, List<AgentStreamEvent> Events);

    private static PlanOperationDto[] ParseDiffOperations(string intent)
    {
        var normalized = Regex.Replace(intent, @"\s+", " ").Trim();
        var matches = Regex.Matches(
            normalized,
            @"(?:^| - )(?<action>Create|Update|Delete)\s+(?<type>Microsoft\.[^\s""]+)\s+""(?<name>[^""]+)""(?<tail>.*?)(?=\s+-\s+(?:Create|Update|Delete)\s+Microsoft\.|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return matches
            .Select(m =>
            {
                var action = NormalizeAction(m.Groups["action"].Value);
                var type = m.Groups["type"].Value.Trim();
                var name = m.Groups["name"].Value.Trim();
                var tail = m.Groups["tail"].Value.Trim().TrimStart(':').Trim();
                var resourceGroup = ExtractResourceGroup(type, name, tail);
                var details = string.IsNullOrWhiteSpace(tail)
                    ? $"Apply requested {action.ToLowerInvariant()} to {type} \"{name}\"."
                    : tail;

                return new PlanOperationDto(action, type, name, resourceGroup, details);
            })
            .ToArray();
    }

    private static string NormalizeAction(string action) =>
        string.Equals(action, "Create", StringComparison.OrdinalIgnoreCase) ? "Create" :
        string.Equals(action, "Delete", StringComparison.OrdinalIgnoreCase) ? "Delete" :
        "Update";

    private static string? ExtractResourceGroup(string type, string name, string tail)
    {
        if (string.Equals(type, "Microsoft.Resources/resourceGroups", StringComparison.OrdinalIgnoreCase))
            return name;

        var match = Regex.Match(tail, @"resource group\s+""(?<rg>[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["rg"].Value : null;
    }

    private static bool IsHighRiskOperation(PlanOperationDto operation) =>
        !string.Equals(operation.Action, "Create", StringComparison.OrdinalIgnoreCase);

    private static string ExtractSessionIdFromIntent(string _) => "deterministic";

    private static bool TryExtractApprovedPlanId(string message, out string planId)
    {
        var match = Regex.Match(
            message,
            @"plan with id\s+(?<id>[0-9a-fA-F-]{36})\s+has been approved",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        planId = match.Success ? match.Groups["id"].Value : "";
        return Guid.TryParse(planId, out _);
    }

    private async Task<DirectToolRun> RunFastInvestigationAsync(
        string subscriptionId,
        State.AgentTaskState taskState,
        string callId,
        CancellationToken ct)
    {
        var events = new List<AgentStreamEvent>();

        Console.WriteLine($"[FastInvestigation] start sub={subscriptionId} rg={taskState.ResourceGroup} components={taskState.RequiredComponents.Count}");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(25));

            var rg = taskState.ResourceGroup!;
            var probes = new List<Task<ProbeResult>>
            {
                ProbeArmIdAsync($"/subscriptions/{subscriptionId}/resourceGroups/{rg}", "resource_group", rg, cts.Token)
            };

            foreach (var component in taskState.RequiredComponents)
            {
                if (string.IsNullOrWhiteSpace(component.ResourceTypeHint)) continue;
                if (string.Equals(component.ResourceTypeHint, "Microsoft.Resources/resourceGroups", StringComparison.OrdinalIgnoreCase))
                    continue;
                var armId = BuildComponentArmId(subscriptionId, rg, component);
                if (armId is null) continue;
                probes.Add(ProbeArmIdAsync(armId, "resource", component.Name, cts.Token));
            }

            Console.WriteLine($"[FastInvestigation] waiting on {probes.Count} probes");
            var probeResults = await Task.WhenAll(probes);
            Console.WriteLine($"[FastInvestigation] probes_done count={probeResults.Length}");

            var rgProbe = probeResults.FirstOrDefault(p => p.Kind == "resource_group");
            var componentProbes = probeResults.Where(p => p.Kind == "resource").ToArray();

            var result = JsonSerializer.Serialize(new
            {
                subscription_id = subscriptionId,
                resource_group = rg,
                resource_group_exists = rgProbe?.Exists ?? false,
                components = componentProbes.Select(p => new
                {
                    name = p.Name,
                    arm_id = p.ResourceId,
                    exists = p.Exists,
                    error = p.ErrorMessage
                }).ToArray(),
                summary = BuildProbeSummary(rgProbe, componentProbes),
                source = "deterministic_fast_path"
            }, OrchestratorTools.SnakeCaseOpts);

            return new DirectToolRun(true, result, events);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var result = JsonSerializer.Serialize(new
            {
                error = true,
                error_type = "transient",
                message = "Azure existence probes exceeded 25s. Proceeding without investigation."
            });
            return new DirectToolRun(false, result, events);
        }
        catch (Exception ex)
        {
            var result = JsonSerializer.Serialize(new { error = true, error_type = "internal", message = ex.Message });
            return new DirectToolRun(false, result, events);
        }
    }

    private async Task<ProbeResult> ProbeArmIdAsync(string armId, string kind, string name, CancellationToken ct)
    {
        Console.WriteLine($"[Probe] start {kind} {name}");
        try
        {
            var result = await _existenceProbe.ProbeAsync(armId, ct);
            Console.WriteLine($"[Probe] done {kind} {name} exists={result.Exists} status={result.HttpStatus}");
            if (result.Exists)
                return new ProbeResult(armId, kind, name, true, null);
            if (result.HttpStatus == 404)
                return new ProbeResult(armId, kind, name, false, null);
            return new ProbeResult(armId, kind, name, false, $"HTTP {result.HttpStatus}: {result.ErrorMessage}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Probe] error {kind} {name} {ex.GetType().Name}: {ex.Message}");
            return new ProbeResult(armId, kind, name, false, ex.Message);
        }
    }

    private static string? BuildComponentArmId(string subscriptionId, string resourceGroup, State.RequiredComponent component)
    {
        if (string.IsNullOrWhiteSpace(component.ResourceTypeHint)) return null;
        if (string.Equals(component.ResourceTypeHint, "Microsoft.Network/virtualNetworks/subnets", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(component.ParentName)) return null;
            var childName = component.Name.Contains('/')
                ? component.Name[(component.Name.IndexOf('/') + 1)..]
                : component.Name;
            return $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Network/virtualNetworks/{component.ParentName}/subnets/{childName}";
        }
        return $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/{component.ResourceTypeHint}/{component.Name}";
    }

    private static string BuildProbeSummary(ProbeResult? rg, ProbeResult[] components)
    {
        var lines = new List<string>();
        lines.Add(rg is null
            ? "Resource group probe skipped."
            : rg.Exists
                ? $"Resource group '{rg.Name}' exists."
                : $"Resource group '{rg.Name}' does NOT exist; create it as part of the plan.");
        if (components.Length == 0)
            lines.Add("No required components to probe.");
        else
        {
            var existing = components.Where(c => c.Exists).Select(c => c.Name).ToArray();
            var missing = components.Where(c => !c.Exists && c.ErrorMessage is null).Select(c => c.Name).ToArray();
            var errored = components.Where(c => c.ErrorMessage is not null).Select(c => $"{c.Name} ({c.ErrorMessage})").ToArray();
            if (existing.Length > 0) lines.Add($"Existing: {string.Join(", ", existing)}.");
            if (missing.Length > 0) lines.Add($"Missing (must create): {string.Join(", ", missing)}.");
            if (errored.Length > 0) lines.Add($"Probe errors: {string.Join(", ", errored)}.");
        }
        return string.Join(" ", lines);
    }

    private sealed record ProbeResult(string ResourceId, string Kind, string Name, bool Exists, string? ErrorMessage);

    private async Task<DirectToolRun> RunDirectToolAsync(
        string toolName,
        Func<Task<string>> run)
    {
        var callId = $"{toolName}_{Guid.NewGuid():N}";
        var events = new List<AgentStreamEvent>
        {
            new AgentStreamEvent.ToolCall(toolName, callId)
        };

        try
        {
            string result;
            using (var trace = AgentStreamTrace.Push())
            {
                result = await run();
                foreach (var evt in trace.Events)
                {
                    if (evt is not AgentStreamEvent.Done)
                        events.Add(evt);
                }
            }

            var success = IsSuccessfulToolResult(result);
            events.Add(new AgentStreamEvent.ToolResult(toolName, callId, success, result));
            return new DirectToolRun(success, result, events);
        }
        catch (Exception ex)
        {
            var result = JsonSerializer.Serialize(new { error = true, message = ex.Message });
            events.Add(new AgentStreamEvent.ToolResult(toolName, callId, false, result));
            return new DirectToolRun(false, result, events);
        }
    }

    private sealed record DirectToolRun(bool Success, string Result, List<AgentStreamEvent> Events);

    private static string BuildRecoveryInvestigationFocus(string planId, string errorType, string message) => $"""
        Execution failed for approved plan {planId}.

        Confirm the actual Azure state before Planner revises the plan. Focus on resources from the failed plan,
        any resources that may have partially succeeded, dependencies that may be missing, SKU/region availability,
        quota or policy blockers, and exact current names/locations.

        Error type: {errorType}
        Error message: {message}
        """;

    private static string BuildRecoveryPlanningIntent(
        string previousPlanId,
        string errorType,
        string message,
        bool investigationSucceeded,
        string investigationResult) => $"""
        Revise the failed approved plan so execution can be retried safely.

        Previous plan id: {previousPlanId}
        Execution error type: {errorType}
        Execution error: {message}

        Investigator confirmation before replan:
        {(investigationSucceeded ? investigationResult : $"Investigation failed or was incomplete: {investigationResult}")}

        Produce a NEW plan that fixes the failure and accounts for any partial Azure state.
        If the fix changes Azure configuration, create the new plan and wait for user approval.
        If the fix needs a human preference, ask a clarification question instead of guessing.
        Do not execute in this turn. After the user approves this revised plan, execution will retry and this same recovery loop will repeat on any later failure.
        """;

    private static bool NeedsRecovery(string json, out string errorType, out string message)
    {
        errorType = "";
        message = "";

        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(json));
            FindRecoveryFields(doc.RootElement, ref errorType, ref message);
        }
        catch
        {
            message = json;
        }

        return !string.IsNullOrWhiteSpace(errorType) ||
               message.Contains("needs_replan", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("InvalidTemplateDeployment", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("validation", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeStructuredExecutionResult(string result)
    {
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(result));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            return TryGetProperty(root, "success", out _) ||
                   TryGetProperty(root, "succeeded", out _) ||
                   TryGetProperty(root, "needs_replan", out _) ||
                   TryGetProperty(root, "error", out _) ||
                   TryGetProperty(root, "error_type", out _) ||
                   TryGetProperty(root, "deployment_name", out _) ||
                   TryGetProperty(root, "resource_id", out _) ||
                   TryGetProperty(root, "operations", out _);
        }
        catch
        {
            return false;
        }
    }

    private static void FindRecoveryFields(JsonElement element, ref string errorType, ref string message)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.NameEquals("needs_replan") && prop.Value.ValueKind == JsonValueKind.True && string.IsNullOrWhiteSpace(errorType))
                        errorType = "needs_replan";
                    else if ((prop.NameEquals("error_type") || prop.NameEquals("errorType")) && prop.Value.ValueKind == JsonValueKind.String)
                        errorType = prop.Value.GetString() ?? errorType;
                    else if ((prop.NameEquals("message") || prop.NameEquals("error_message") || prop.NameEquals("ErrorMessage")) && prop.Value.ValueKind == JsonValueKind.String)
                        message = AppendMessage(message, prop.Value.GetString());

                    FindRecoveryFields(prop.Value, ref errorType, ref message);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    FindRecoveryFields(item, ref errorType, ref message);
                break;
            case JsonValueKind.String:
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value) &&
                    (value.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
                     value.Contains("InvalidTemplateDeployment", StringComparison.OrdinalIgnoreCase) ||
                     value.Contains("validation", StringComparison.OrdinalIgnoreCase)))
                    message = AppendMessage(message, value);
                break;
        }

        if (message.Contains("quota", StringComparison.OrdinalIgnoreCase))
            errorType = "quota";
    }

    private static string AppendMessage(string existing, string? next)
    {
        if (string.IsNullOrWhiteSpace(next))
            return existing;
        if (existing.Contains(next, StringComparison.OrdinalIgnoreCase))
            return existing;
        return string.IsNullOrWhiteSpace(existing) ? next : $"{existing}\n{next}";
    }

    private static bool TryExtractPlanId(string json, out string planId)
    {
        planId = "";
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(json));
            if (!doc.RootElement.TryGetProperty("plan_id", out var planIdEl))
                return false;
            planId = planIdEl.GetString() ?? "";
            return Guid.TryParse(planId, out _);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractPlanIdFromEvents(IEnumerable<AgentStreamEvent> events, out string planId)
    {
        foreach (var evt in events)
        {
            if (evt is AgentStreamEvent.ToolResult tr &&
                tr.Success &&
                string.Equals(tr.ToolName, "create_plan", StringComparison.OrdinalIgnoreCase) &&
                TryExtractPlanId(tr.ResultJson, out planId))
                return true;
        }

        planId = "";
        return false;
    }

    private static bool TryExtractQuestionId(string json, out string questionId)
    {
        questionId = "";
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(json));
            if (!doc.RootElement.TryGetProperty("question_id", out var questionIdEl))
                return false;
            questionId = questionIdEl.GetString() ?? "";
            return Guid.TryParse(questionId, out _);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractCriticVerdict(string json, out bool approved, out string feedback)
    {
        approved = false;
        feedback = "";
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(json));
            if (!doc.RootElement.TryGetProperty("approved", out var approvedEl) ||
                approvedEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return false;

            approved = approvedEl.GetBoolean();
            if (doc.RootElement.TryGetProperty("feedback", out var feedbackEl) &&
                feedbackEl.ValueKind == JsonValueKind.String)
                feedback = feedbackEl.GetString() ?? "";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildCriticRevisionIntent(string originalIntent, string rejectedPlanId, string feedback) => $"""
        Revise the deployment plan rejected by Critic.

        Original user intent:
        {originalIntent}

        Rejected plan id: {rejectedPlanId}

        Critic feedback to fix:
        {feedback}

        Produce a new complete plan that addresses every critic issue. If a resource group already
        exists and the deployment is resource-group scoped, do not include a standalone resource group
        tag update unless the template also applies it correctly. If resource group tag changes are
        required, use a subscription-scoped template that includes the resource group update.
        """;

    private static bool IsBlockingCriticFeedback(string feedback)
    {
        if (string.IsNullOrWhiteSpace(feedback)) return false;

        string[] blockers =
        {
            "missing template_json",
            "missing_template_json",
            "invalid_template_json",
            "not valid JSON",
            "policy violation",
            "policy_violation",
            "no-compute",
            "disallowed",
            "sku under properties",
            "missing top-level sku",
            "missing top-level resourceGroup",
            "missing resourceGroup",
            "operation-to-template mismatch",
            "operations_template_mismatch",
            "resources not listed in operations",
            "operations reference resources not present"
        };

        return blockers.Any(b => feedback.Contains(b, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSuccessfulToolResult(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(json));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return true;

            if (TryGetProperty(root, "success", out var successEl) && successEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return successEl.GetBoolean();
            if (TryGetProperty(root, "succeeded", out var succeededEl) && succeededEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return succeededEl.GetBoolean();
            if (TryGetProperty(root, "needs_replan", out var needsReplanEl) && needsReplanEl.ValueKind == JsonValueKind.True)
                return false;
            if (TryGetProperty(root, "error_type", out var errorTypeEl) &&
                errorTypeEl.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(errorTypeEl.GetString()))
                return false;
            if (TryGetProperty(root, "error", out var errorEl) && IsErrorValue(errorEl))
                return false;

            return true;
        }
        catch
        {
            return !json.Contains("\"error\":true", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.TryGetProperty(name, out value))
            return true;

        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool IsErrorValue(JsonElement errorEl) => errorEl.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Object => true,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(errorEl.GetString()),
        _ => false
    };

    private static string? ExtractTemplateHashFromPlanData(JsonElement? planData)
    {
        if (planData is null) return null;
        if (!TryGetProperty(planData.Value, "template_json", out var templateEl)) return null;
        var text = templateEl.ValueKind == JsonValueKind.String ? templateEl.GetString() : templateEl.GetRawText();
        return string.IsNullOrWhiteSpace(text) ? null : IntentParser.ComputeHash(text);
    }

    private static string ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return text;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (ch == '\\' && inString)
            {
                escaped = true;
                continue;
            }
            if (ch == '"')
            {
                inString = !inString;
                continue;
            }
            if (inString) continue;

            if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                    return text[start..(i + 1)];
            }
        }

        return text[start..];
    }

    private static string PreviewForLog(string value)
    {
        const int max = 900;
        if (string.IsNullOrEmpty(value)) return "";
        var normalized = Regex.Replace(value, "\\s+", " ").Trim();
        return normalized.Length <= max ? normalized : normalized[..max] + "...";
    }

    private static bool ShouldRetryForMissingProgress(string message, string finalText)
    {
        if (!LooksLikeInfrastructureWork(message))
            return false;

        if (string.IsNullOrWhiteSpace(finalText))
            return true;

        return Regex.IsMatch(
            finalText,
            @"\b(I'?ll|I will|Let me|I can|I'?m going to|I am going to)\b.*\b(investigate|plan|start|help|apply|check)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    private static bool LooksLikeInfrastructureWork(string message) =>
        Regex.IsMatch(
            message,
            @"\b(apply|update|create|delete|deploy|change|plan|execute|provision|modify)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string BuildRequiredProgressMessage(string originalMessage, string firstResponse) => $"""
        Continue the same user request now.

        You previously replied with text but did not call any orchestration tools:
        {firstResponse}

        This is not sufficient. For infrastructure work, you MUST make progress by calling the appropriate tool now:
        - For requested changes: call investigate_infrastructure if current state matters, then call plan_deployment.
        - If planning is blocked by ambiguity: call ask_clarifying_question.
        - Do not reply with another "I'll investigate" or "I'll plan" statement.

        Original user request:
        {originalMessage}
        """;

    private bool ShouldRunDeterministicIntentPlanning(
        string sessionId,
        string? pendingApproval,
        string? pendingQuestionAnswer)
    {
        var reason = "";
        try
        {
            if (!string.IsNullOrWhiteSpace(pendingApproval)) { reason = "pending_approval"; return false; }
            if (!string.IsNullOrWhiteSpace(pendingQuestionAnswer)) { reason = "pending_question"; return false; }

            var state = _store.GetTaskState(sessionId);
            if (state is null) { reason = "state_null"; return false; }
            if (!state.HasIntent) { reason = "no_intent"; return false; }
            if (state.RequiredComponents.Count == 0) { reason = "no_components"; return false; }
            if (state.CandidatePlanId is not null) { reason = "candidate_plan_set"; return false; }
            if (state.ApprovedPlanId is not null) { reason = "approved_plan_set"; return false; }
            reason = "deterministic";
            return true;
        }
        finally
        {
            Console.WriteLine($"[AgentService] route_decision session={sessionId} path={(reason == "deterministic" ? "DETERMINISTIC" : "LLM_ORCHESTRATOR")} reason={reason}");
        }
    }

    public void ResumeAfterPlanApproval(string sessionId, Guid planId) =>
        _store.SetPendingApproval(sessionId, planId);

    public void ResumeAfterQuestionAnswer(string sessionId, Guid questionId, string answer) =>
        _store.SetPendingQuestionAnswer(sessionId, questionId, answer);
}
