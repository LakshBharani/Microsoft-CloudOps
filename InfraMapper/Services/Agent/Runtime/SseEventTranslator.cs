using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using InfraMapper.Services.Agent.Tools;

namespace InfraMapper.Services.Agent.Runtime;

public sealed class SseEventTranslator
{
    private readonly string _sessionId;
    private readonly PlanStore _planStore;
    private readonly QuestionStore _questionStore;
    private readonly bool _autoApprovePlan;

    public static readonly HashSet<string> SubAgentToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "investigate_infrastructure",
        "plan_deployment",
        "critique_plan",
        "ask_clarifying_question",
        "execute_plan",
        "reflect_on_deployment",
    };

    // Tracks iteration counts per sub-agent tool across one streaming run.
    private readonly Dictionary<string, int> _subAgentIterations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string agentName, int iteration)> _pendingSubAgentCalls = new();

    // Phase 3: plan event buffering for silent retry UX.
    private string? _bufferedPlanResultJson;  // raw result JSON from plan_deployment; null until buffered
    private int _planRevisionCount;           // incremented each time Critic rejects
    private readonly HashSet<string> _emittedQuestionIds = new(StringComparer.OrdinalIgnoreCase);

    public SseEventTranslator(string sessionId, PlanStore planStore, QuestionStore questionStore, bool autoApprovePlan)
    {
        _sessionId = sessionId;
        _planStore = planStore;
        _questionStore = questionStore;
        _autoApprovePlan = autoApprovePlan;
    }

    public async IAsyncEnumerable<string> TranslateAsync(
        IAsyncEnumerable<AgentStreamEvent> stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var textBuilder = new StringBuilder();
        long totalInput = 0, totalOutput = 0;
        string? errorMessage = null;

        await foreach (var evt in stream.WithCancellation(ct))
        {
            switch (evt)
            {
                case AgentStreamEvent.ToolCall tc:
                {
                    var toolName = tc.ToolName;
                    var callId   = tc.CallId;

                    yield return Evt("tool_call", new { tool = toolName, session_id = _sessionId });
                    yield return Evt("activity_start", new
                    {
                        id = callId ?? toolName,
                        parent_id = (string?)null,
                        kind = SubAgentToolNames.Contains(toolName) ? "agent" : "tool",
                        agent = SubAgentToolNames.Contains(toolName) ? toolName : null,
                        tool = SubAgentToolNames.Contains(toolName) ? null : toolName,
                        model = SubAgentToolNames.Contains(toolName) ? AgentRegistry.GetModel(ToolNameToAgentName(toolName)) : null,
                        status = "running",
                        summary = ActivityStartSummary(toolName),
                        session_id = _sessionId
                    });

                    if (SubAgentToolNames.Contains(toolName))
                    {
                        _subAgentIterations.TryGetValue(toolName, out var prev);
                        var iter = prev + 1;
                        _subAgentIterations[toolName] = iter;

                        if (callId is not null)
                            _pendingSubAgentCalls[callId] = (toolName, iter);

                        var model = AgentRegistry.GetModel(ToolNameToAgentName(toolName));
                        yield return Evt("agent_call", new
                        {
                            agent      = toolName,
                            model,
                            iteration  = iter,
                            parent_tool_call_id = callId,
                            session_id = _sessionId
                        });
                    }
                    break;
                }

                case AgentStreamEvent.ToolResult tr:
                {
                    var toolName  = tr.ToolName;
                    var callId    = tr.CallId;
                    var resultStr = tr.ResultJson;
                    var isError   = !tr.Success;

                    yield return Evt("tool_result", new { tool = toolName, success = tr.Success, session_id = _sessionId });
                    var (errorType, message) = SummarizeResult(resultStr);
                    yield return Evt("activity_end", new
                    {
                        id = callId ?? toolName,
                        kind = SubAgentToolNames.Contains(toolName) ? "agent" : "tool",
                        agent = SubAgentToolNames.Contains(toolName) ? toolName : null,
                        tool = SubAgentToolNames.Contains(toolName) ? null : toolName,
                        status = tr.Success ? "success" : "failed",
                        summary = tr.Success ? ActivityEndSummary(toolName) : $"{ToolDisplayName(toolName)} failed",
                        detail_preview = Preview(resultStr),
                        error_type = errorType,
                        message,
                        session_id = _sessionId
                    });

                    if (callId is not null && _pendingSubAgentCalls.TryGetValue(callId, out var pending))
                    {
                        _pendingSubAgentCalls.Remove(callId);
                        yield return Evt("agent_result", new
                        {
                            agent         = pending.agentName,
                            success       = !isError,
                            iteration     = pending.iteration,
                            input_tokens  = 0,
                            output_tokens = 0,
                            session_id    = _sessionId
                        });
                    }

                    if (IsQuestionResult(resultStr))
                    {
                        foreach (var e in BuildQuestionEvents(resultStr))
                            yield return e;
                        _bufferedPlanResultJson = null;
                        break;
                    }

                    // Buffer plan events from plan_deployment (Phase 2) or create_plan (Phase 0/1 fallback).
                    // The plan SSE event is held until the Critic approves or the stream ends.
                    if ((toolName == "plan_deployment" || toolName == "create_plan") && !isError)
                    {
                        _bufferedPlanResultJson = resultStr;
                    }

                    // Phase 3: Critic result decides whether to emit or discard the buffered plan.
                    if (toolName == "critique_plan" && !isError)
                    {
                        foreach (var e in HandleCritiqueResult(resultStr))
                            yield return e;
                    }
                    if (toolName == "ask_clarifying_question" && !isError)
                    {
                        foreach (var e in BuildQuestionEvents(resultStr))
                            yield return e;
                    }
                    break;
                }

                case AgentStreamEvent.Activity a:
                    yield return Evt(a.Phase == "start" ? "activity_start" : "activity_end", new
                    {
                        id = a.Id,
                        parent_id = a.ParentId,
                        kind = a.Kind,
                        agent = a.Agent,
                        tool = a.Tool,
                        model = a.Model,
                        status = a.Status,
                        summary = a.Summary,
                        detail_preview = a.DetailPreview,
                        error_type = a.ErrorType,
                        message = a.Message,
                        session_id = _sessionId
                    });
                    break;

                case AgentStreamEvent.Usage u:
                    totalInput  += u.InputTokens;
                    totalOutput += u.OutputTokens;
                    break;

                case AgentStreamEvent.Done d:
                    textBuilder.Append(d.Text);
                    break;

                case AgentStreamEvent.Error e:
                    errorMessage = e.Message;
                    break;
            }
        }

        if (errorMessage is not null)
        {
            yield return Evt("error", new { message = errorMessage, session_id = _sessionId });
            yield break;
        }

        // If a plan was buffered but never critiqued (Phase 0/1 compatibility or auto-approve path),
        // emit it now at end of stream.
        if (_bufferedPlanResultJson is not null && _emittedQuestionIds.Count == 0)
        {
            foreach (var evt in EmitBufferedPlan())
                yield return evt;
        }

        if (totalInput > 0 || totalOutput > 0)
            yield return Evt("usage", new
            {
                input_tokens  = (int)totalInput,
                output_tokens = (int)totalOutput,
                session_id    = _sessionId
            });

        var text = textBuilder.ToString();
        if (_emittedQuestionIds.Count == 0 && LooksLikePlainTextQuestion(text))
        {
            foreach (var evt in BuildPlainTextFallbackQuestion(text))
                yield return evt;
            text = "Please answer the clarification.";
        }

        yield return Evt("reply", new
        {
            content    = text.Length > 0 ? text : "Done.",
            session_id = _sessionId
        });
    }

    // ─── Plan buffering helpers ──────────────────────────────────────────────

    private IEnumerable<string> HandleCritiqueResult(string critiqueJson)
    {
        bool? approved = null;
        string? feedback = null;
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(critiqueJson));
            if (doc.RootElement.TryGetProperty("approved", out var approvedEl))
                approved = approvedEl.GetBoolean();
            if (doc.RootElement.TryGetProperty("feedback", out var feedbackEl))
                feedback = feedbackEl.GetString();
        }
        catch { /* malformed — treat as approved to avoid blocking */ }

        if (approved == false)
        {
            // Critic rejected: discard the buffered plan. The activity stream still carries
            // the critic result, but rejected plans should not be shown as approvable plans.
            _bufferedPlanResultJson = null;
            _planRevisionCount++;
            yield break;
        }

        // Critic approved (or result unparseable): emit the buffered plan.
        foreach (var evt in EmitBufferedPlan())
            yield return evt;
    }

    private IEnumerable<string> EmitBufferedPlan()
    {
        if (_bufferedPlanResultJson is null) yield break;

        foreach (var evt in BuildPlanEvents(_bufferedPlanResultJson, _planRevisionCount, "pending"))
            yield return evt;

        _bufferedPlanResultJson = null;
    }

    private IEnumerable<string> BuildPlanEvents(
        string resultJson,
        int revisionCount,
        string status,
        string? criticFeedbackOverride = null)
    {
        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(ExtractJsonObject(resultJson)); }
        catch { yield break; }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("plan_id", out var planIdEl) ||
                !Guid.TryParse(planIdEl.GetString(), out var planGuid))
                yield break;

            if (_autoApprovePlan)
            {
                _planStore.TryApprove(planGuid, out _);
                yield return Evt("plan_auto_approved", new { plan_id = planGuid, session_id = _sessionId });
            }
            else
            {
                var ops = root.TryGetProperty("operations", out var opsEl)
                    ? JsonSerializer.Deserialize<object[]>(opsEl.GetRawText())
                    : Array.Empty<object>();

                yield return Evt("plan", new
                {
                    plan_id = planIdEl.GetString(),
                    title   = root.TryGetProperty("title", out var t) ? t.GetString() : null,
                    operations = ops,
                    risk_level = root.TryGetProperty("risk_level", out var r) ? r.GetString() : "Medium",
                    estimated_cost_note = root.TryGetProperty("estimated_cost_note", out var e) && e.ValueKind != JsonValueKind.Null
                        ? e.GetString() : null,
                    critic_verdict = criticFeedbackOverride ?? _planStore.GetCriticInfo(planGuid).verdict,
                    revision_count = revisionCount,
                    status,
                    session_id     = _sessionId
                });
            }
        }
    }

    private IEnumerable<string> BuildQuestionEvents(string resultJson)
    {
        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(ExtractJsonObject(resultJson)); }
        catch { yield break; }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("question_id", out var questionIdEl))
                yield break;
            var questionId = questionIdEl.GetString();
            if (string.IsNullOrWhiteSpace(questionId) || !_emittedQuestionIds.Add(questionId))
                yield break;

            yield return Evt("question", new
            {
                question_id = questionId,
                title = root.TryGetProperty("title", out var t) ? t.GetString() : "Clarify request",
                prompt = root.TryGetProperty("prompt", out var p) ? p.GetString() : "",
                options = root.TryGetProperty("options", out var o)
                    ? JsonSerializer.Deserialize<object[]>(o.GetRawText())
                    : Array.Empty<object>(),
                default_value = root.TryGetProperty("default_value", out var d) ? d.GetString() : null,
                allow_custom = !root.TryGetProperty("allow_custom", out var ac) || ac.GetBoolean(),
                category = root.TryGetProperty("category", out var cat) ? cat.GetString() : null,
                confirmation_scope = root.TryGetProperty("confirmation_scope", out var scope) ? scope.GetString() : null,
                originating_agent = root.TryGetProperty("originating_agent", out var origin) ? origin.GetString() : null,
                session_id = _sessionId
            });
        }
    }

    private IEnumerable<string> BuildPlainTextFallbackQuestion(string prompt)
    {
        var options = new[]
        {
            new QuestionOptionDto("Confirm", "confirm", "The listed details are correct; continue planning."),
            new QuestionOptionDto("Needs correction", "needs_correction", "I will provide corrections before planning continues.")
        };

        var questionData = JsonSerializer.SerializeToElement(new
        {
            title = "Confirm infrastructure details",
            prompt,
            options,
            default_value = "confirm",
            allow_custom = true,
            category = "general",
            confirmation_scope = (string?)null,
            originating_agent = "orchestrator"
        }, OrchestratorTools.SnakeCaseOpts);

        var questionId = _questionStore.CreateQuestion(_sessionId, questionData).ToString();
        _emittedQuestionIds.Add(questionId);

        yield return Evt("question", new
        {
            question_id = questionId,
            title = "Confirm infrastructure details",
            prompt,
            options,
            default_value = "confirm",
            allow_custom = true,
            category = "general",
            confirmation_scope = (string?)null,
            originating_agent = "orchestrator",
            session_id = _sessionId
        });
    }

    private static bool LooksLikePlainTextQuestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains('?'))
            return false;

        return ContainsAny(text,
            "could you please confirm",
            "please confirm",
            "provide any corrections",
            "need to confirm",
            "can you confirm",
            "would you like",
            "which option",
            "choose");
    }

    private static bool IsQuestionResult(string resultJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(resultJson));
            return doc.RootElement.TryGetProperty("question_id", out _);
        }
        catch { return false; }
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(n => value.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static string ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start
            ? text[start..(end + 1)]
            : text;
    }

    private static (string? errorType, string? message) SummarizeResult(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(json));
            var root = doc.RootElement;
            var errorType = root.TryGetProperty("error_type", out var et) ? et.GetString() : null;
            var message = root.TryGetProperty("message", out var msg) ? msg.GetString() :
                root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String ? err.GetString() : null;
            return (errorType, message);
        }
        catch { return (null, null); }
    }

    private static string Preview(string value)
    {
        const int max = 900;
        var redacted = Redact(value);
        return redacted.Length <= max ? redacted : redacted[..max] + "…";
    }

    private static string Redact(string value) =>
        System.Text.RegularExpressions.Regex.Replace(
            value,
            "(?i)(pat|token|key|secret|authorization|connectionstring|connection_string)(\"?\\s*[:=]\\s*\"?)[^\",}\\s]+",
            "$1$2[redacted]");

    private static string ActivityStartSummary(string toolName) => $"{ToolDisplayName(toolName)} started";
    private static string ActivityEndSummary(string toolName) => $"{ToolDisplayName(toolName)} completed";

    private static string ToolDisplayName(string toolName) => toolName switch
    {
        "investigate_infrastructure" => "Investigator",
        "plan_deployment" => "Planner",
        "critique_plan" => "Critic",
        "ask_clarifying_question" => "Questioner",
        "execute_plan" => "Executor",
        "reflect_on_deployment" => "Reflector",
        "get_lessons" => "Lessons learned",
        _ => toolName
    };

    private static string ToolNameToAgentName(string toolName) => toolName switch
    {
        "investigate_infrastructure" => "investigator",
        "plan_deployment"            => "planner",
        "critique_plan"              => "critic",
        "ask_clarifying_question"    => "questioner",
        "execute_plan"               => "executor",
        "reflect_on_deployment"      => "reflector",
        _                            => toolName
    };

    internal static string Evt(string type, object data) =>
        JsonSerializer.Serialize(new { type, data = JsonSerializer.SerializeToElement(data) });
}
