using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace InfraMapper.Services.Agent.Runtime;

public sealed class SseEventTranslator
{
    private readonly string _sessionId;
    private readonly PlanStore _planStore;

    public SseEventTranslator(string sessionId, PlanStore planStore)
    {
        _sessionId = sessionId;
        _planStore = planStore;
    }

    public async IAsyncEnumerable<string> TranslateAsync(
        IAsyncEnumerable<AgentStreamEvent> stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var textBuilder = new StringBuilder();
        long totalInput = 0;
        long totalOutput = 0;

        await foreach (var evt in stream.WithCancellation(ct))
        {
            switch (evt)
            {
                case AgentStreamEvent.ToolCall tc:
                    yield return Evt("tool_call", new { tool = tc.ToolName, session_id = _sessionId });
                    yield return Evt("activity_start", new
                    {
                        id = tc.CallId ?? tc.ToolName,
                        parent_id = (string?)null,
                        kind = "tool",
                        agent = (string?)null,
                        tool = tc.ToolName,
                        model = (string?)null,
                        status = "running",
                        summary = $"Calling {tc.ToolName}",
                        session_id = _sessionId
                    });
                    break;

                case AgentStreamEvent.ToolResult tr:
                    yield return Evt("tool_result", new { tool = tr.ToolName, success = tr.Success, session_id = _sessionId });
                    var (errorType, message) = SummarizeResult(tr.ResultJson);
                    yield return Evt("activity_end", new
                    {
                        id = tr.CallId ?? tr.ToolName,
                        kind = "tool",
                        agent = (string?)null,
                        tool = tr.ToolName,
                        status = tr.Success ? "success" : "failed",
                        summary = tr.Success ? $"{tr.ToolName} completed" : $"{tr.ToolName} failed",
                        detail_preview = Preview(tr.ResultJson),
                        error_type = errorType,
                        message,
                        session_id = _sessionId
                    });

                    if (string.Equals(tr.ToolName, "create_plan", StringComparison.OrdinalIgnoreCase) && tr.Success)
                    {
                        foreach (var planEvt in BuildPlanEvents(tr.ResultJson))
                            yield return planEvt;
                    }
                    break;

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
                    totalInput += u.InputTokens;
                    totalOutput += u.OutputTokens;
                    break;

                case AgentStreamEvent.Done d:
                    textBuilder.Append(d.Text);
                    break;

                case AgentStreamEvent.Error e:
                    yield return Evt("error", new { message = e.Message, session_id = _sessionId });
                    yield break;
            }
        }

        if (totalInput > 0 || totalOutput > 0)
            yield return Evt("usage", new { input_tokens = (int)totalInput, output_tokens = (int)totalOutput, session_id = _sessionId });

        var text = textBuilder.ToString();
        yield return Evt("reply", new { content = text.Length > 0 ? text : "Done.", session_id = _sessionId });
    }

    private IEnumerable<string> BuildPlanEvents(string resultJson)
    {
        if (!AgentResultParser.TryParse(resultJson, out var parsed) ||
            string.IsNullOrWhiteSpace(parsed.PlanId) ||
            !Guid.TryParse(parsed.PlanId, out var planGuid))
            yield break;

        var planData = _planStore.GetPlanData(planGuid);
        if (planData is null)
            yield break;

        var root = planData.Value;
        var ops = root.TryGetProperty("operations", out var opsEl)
            ? JsonSerializer.Deserialize<object[]>(opsEl.GetRawText())
            : Array.Empty<object>();

        yield return Evt("plan", new
        {
            plan_id = planGuid,
            title = root.TryGetProperty("title", out var title) ? title.GetString() : "Azure infrastructure plan",
            operations = ops,
            risk_level = root.TryGetProperty("risk_level", out var risk) ? risk.GetString() : "Medium",
            estimated_cost_note = (string?)null,
            revision_count = 0,
            status = "approved",
            session_id = _sessionId
        });
    }

    private static (string? errorType, string? message) SummarizeResult(string json)
    {
        if (AgentResultParser.TryParse(json, out var parsed) &&
            (!string.IsNullOrWhiteSpace(parsed.ErrorType) || !string.IsNullOrWhiteSpace(parsed.Message)))
            return (parsed.ErrorType, parsed.Message);

        return (null, null);
    }

    private static string Preview(string value)
    {
        const int max = 900;
        return value.Length <= max ? value : value[..max] + "...";
    }

    public static string Evt(string type, object data) => JsonSerializer.Serialize(new { type, data });
}
