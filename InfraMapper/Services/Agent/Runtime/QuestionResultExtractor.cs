using System.Text.Json;

namespace InfraMapper.Services.Agent.Runtime;

internal static class QuestionResultExtractor
{
    public static bool HasQuestionId(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start < 0 || end <= start) return false;
            using var doc = JsonDocument.Parse(json[start..(end + 1)]);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("question_id", out _);
        }
        catch { return false; }
    }

    public static string? FindLastQuestionResult(IEnumerable<AgentStreamEvent> events)
    {
        return events
            .OfType<AgentStreamEvent.ToolResult>()
            .Where(r =>
                string.Equals(r.ToolName, "create_question", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.ToolName, "ask_clarifying_question", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.ResultJson)
            .LastOrDefault(HasQuestionId);
    }
}
