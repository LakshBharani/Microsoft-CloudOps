using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using InfraMapper.Models.Agent;
using InfraMapper.Services.Agent.Runtime;
using InfraMapper.Services.Agent.Tools;
using Microsoft.SemanticKernel.Agents;

namespace InfraMapper.Services.Agent;

public sealed class AgentService
{
    private readonly ConversationStore _store;
    private readonly PlanStore _planStore;
    private readonly QuestionStore _questionStore;
    private readonly SkAgentRunner _runner;

    public AgentService(ConversationStore store, PlanStore planStore, QuestionStore questionStore, SkAgentRunner runner)
    {
        _store = store;
        _planStore = planStore;
        _questionStore = questionStore;
        _runner = runner;
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
                ? RunDeterministicExecutionAsync(entry!, approvedPlanId, request.SubscriptionId, ct)
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

    private async IAsyncEnumerable<AgentStreamEvent> RunDeterministicExecutionAsync(
        ConversationStore.SessionEntry entry,
        string planId,
        string subscriptionId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var executeCallId = $"execute_plan_{Guid.NewGuid():N}";
        yield return new AgentStreamEvent.ToolCall("execute_plan", executeCallId);

        var data = Guid.TryParse(planId, out var planGuid)
            ? _planStore.GetPlanData(planGuid)
            : null;
        if (data is null)
        {
            yield return new AgentStreamEvent.ToolResult(
                "execute_plan",
                executeCallId,
                false,
                JsonSerializer.Serialize(new { error = true, error_type = "missing_plan", message = "Approved plan not found or expired." }));
            yield break;
        }

        var operations = ExtractPlanOperations(data.Value);
        if (operations.Length == 0)
        {
            yield return new AgentStreamEvent.ToolResult(
                "execute_plan",
                executeCallId,
                false,
                JsonSerializer.Serialize(new { error = true, error_type = "empty_plan", message = "Approved plan contains no operations." }));
            yield break;
        }

        var results = new List<object>();
        var allSucceeded = true;
        foreach (var operation in operations)
        {
            ct.ThrowIfCancellationRequested();
            var opResult = await ApplyOperationAsync(entry.ExecutorTools, planId, subscriptionId, operation, ct);
            foreach (var evt in opResult.Events)
                yield return evt;
            results.Add(new
            {
                operation = operation.ResourceName,
                action = operation.Action,
                success = opResult.Success,
                result = TryParseJson(opResult.Result)
            });
            allSucceeded &= opResult.Success;
            if (!opResult.Success)
                break;
        }

        yield return new AgentStreamEvent.ToolResult(
            "execute_plan",
            executeCallId,
            allSucceeded,
            JsonSerializer.Serialize(new
            {
                success = allSucceeded,
                plan_id = planId,
                operations = results,
                message = allSucceeded ? "Approved plan executed." : "Approved plan execution stopped after a failed operation."
            }, OrchestratorTools.SnakeCaseOpts));
    }

    private async Task<DirectToolRun> ApplyOperationAsync(
        ExecutorTools tools,
        string planId,
        string subscriptionId,
        PlanOperationDto operation,
        CancellationToken ct)
    {
        var callId = $"apply_resource_mutation_{Guid.NewGuid():N}";
        var events = new List<AgentStreamEvent>
        {
            new AgentStreamEvent.ToolCall("apply_resource_mutation", callId)
        };

        try
        {
            var resourceId = BuildResourceId(subscriptionId, operation);
            var operationKind = string.Equals(operation.Action, "Delete", StringComparison.OrdinalIgnoreCase)
                ? "Delete"
                : "CreateOrUpdate";
            var result = await tools.ApplyResourceMutationAsync(
                planId,
                resourceId,
                operationKind,
                ExtractLocation(operation.Details),
                ExtractJsonField(operation.Details, "properties"),
                ExtractTags(operation.Details),
                ExtractJsonField(operation.Details, "sku") ?? ExtractSku(operation.Details),
                ExtractKind(operation.Details),
                ct);
            var success = IsSuccessfulToolResult(result);
            events.Add(new AgentStreamEvent.ToolResult("apply_resource_mutation", callId, success, result));
            return new DirectToolRun(success, result, events);
        }
        catch (Exception ex)
        {
            var result = JsonSerializer.Serialize(new { error = true, error_type = "internal", message = ex.Message });
            events.Add(new AgentStreamEvent.ToolResult("apply_resource_mutation", callId, false, result));
            return new DirectToolRun(false, result, events);
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

        await foreach (var evt in RunDeterministicPlanningAsync(entry.Orchestrator, message, subscriptionId, ct))
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
        string intent,
        string subscriptionId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var investigation = await RunDirectToolAsync(
            "investigate_infrastructure",
            () => orchestrator.InvestigateInfrastructure(intent, subscriptionId, ct));
        foreach (var evt in investigation.Events)
            yield return evt;
        if (!investigation.Success)
            yield break;

        var plan = await RunDirectToolAsync(
            "plan_deployment",
            () => orchestrator.PlanDeployment(intent, investigation.Result, ct));
        foreach (var evt in plan.Events)
            yield return evt;
        if (!plan.Success || TryExtractQuestionId(plan.Result, out _))
            yield break;

        if (!TryExtractPlanId(plan.Result, out var planId))
        {
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

    private static PlanOperationDto[] ExtractPlanOperations(JsonElement planData)
    {
        if (!TryGetProperty(planData, "operations", out var operationsEl) || operationsEl.ValueKind != JsonValueKind.Array)
            return Array.Empty<PlanOperationDto>();

        string? defaultResourceGroup = null;
        var operations = new List<PlanOperationDto>();
        foreach (var operationEl in operationsEl.EnumerateArray())
        {
            var action = GetString(operationEl, "action") ?? "Update";
            var type = GetString(operationEl, "resource_type") ?? GetString(operationEl, "resourceType");
            var name = GetString(operationEl, "resource_name") ?? GetString(operationEl, "resourceName");
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(name))
                continue;

            var resourceGroup = GetString(operationEl, "resource_group") ?? GetString(operationEl, "resourceGroup");
            if (string.Equals(type, "Microsoft.Resources/resourceGroups", StringComparison.OrdinalIgnoreCase))
                defaultResourceGroup ??= name;

            operations.Add(new PlanOperationDto(
                NormalizeAction(action),
                type,
                name,
                resourceGroup,
                GetString(operationEl, "details")));
        }

        if (string.IsNullOrWhiteSpace(defaultResourceGroup))
            return operations.ToArray();

        return operations
            .Select(operation =>
                string.Equals(operation.ResourceType, "Microsoft.Resources/resourceGroups", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(operation.ResourceGroup)
                    ? operation
                    : operation with { ResourceGroup = defaultResourceGroup })
            .ToArray();
    }

    private static string? GetString(JsonElement root, string name) =>
        TryGetProperty(root, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string BuildResourceId(string subscriptionId, PlanOperationDto operation)
    {
        if (string.Equals(operation.ResourceType, "Microsoft.Resources/resourceGroups", StringComparison.OrdinalIgnoreCase))
            return $"/subscriptions/{subscriptionId}/resourceGroups/{operation.ResourceName}";

        var resourceGroup = operation.ResourceGroup;
        if (string.IsNullOrWhiteSpace(resourceGroup))
            resourceGroup = ExtractResourceGroup(operation.ResourceType, operation.ResourceName, operation.Details ?? "");
        if (string.IsNullOrWhiteSpace(resourceGroup))
            throw new InvalidOperationException($"Resource group is required for {operation.ResourceType} \"{operation.ResourceName}\".");

        return $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/{operation.ResourceType}/{operation.ResourceName}";
    }

    private static object? TryParseJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            return json;
        }
    }

    private static string? ExtractLocation(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
            return "eastus";

        var match = Regex.Match(details, @"\b(?:location|region)\s*[:=]\s*""?(?<location>[a-z0-9 -]+)""?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["location"].Value.Trim() : "eastus";
    }

    private static string? ExtractKind(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
            return null;

        var match = Regex.Match(details, @"kind\s*:\s*(?<to>[^,]+?)\s*(?:$|,)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return null;

        var value = match.Groups["to"].Value.Trim();
        var arrow = value.LastIndexOf('→');
        return arrow >= 0 ? value[(arrow + 1)..].Trim() : value;
    }

    private static string? ExtractSku(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
            return null;

        var match = Regex.Match(details, @"sku\s*:\s*(?:\{.*?\}\s*→\s*)?(?<sku>\{.*?\})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["sku"].Value : null;
    }

    private static string? ExtractJsonField(string? details, string field)
    {
        if (string.IsNullOrWhiteSpace(details))
            return null;

        var match = Regex.Match(details, $@"""{Regex.Escape(field)}""\s*:\s*(?<json>\{{.*?\}})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["json"].Value : null;
    }

    private static Dictionary<string, string>? ExtractTags(string? details)
    {
        if (string.IsNullOrWhiteSpace(details) || !details.Contains("tag:", StringComparison.OrdinalIgnoreCase))
            return null;

        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(
            details,
            @"tag:(?<key>[A-Za-z0-9_.-]+)\s*:\s*[^,]+?\s*→\s*(?<value>[^,]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            tags[match.Groups["key"].Value] = match.Groups["value"].Value.Trim();
        }

        return tags.Count > 0 ? tags : null;
    }

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

    private static string ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start
            ? text[start..(end + 1)]
            : text;
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

    public void ResumeAfterPlanApproval(string sessionId, Guid planId) =>
        _store.SetPendingApproval(sessionId, planId);

    public void ResumeAfterQuestionAnswer(string sessionId, Guid questionId, string answer) =>
        _store.SetPendingQuestionAnswer(sessionId, questionId, answer);
}
