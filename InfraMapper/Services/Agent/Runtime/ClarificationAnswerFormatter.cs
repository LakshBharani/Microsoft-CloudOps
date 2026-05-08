using System.Text;
using System.Text.Json;

namespace InfraMapper.Services.Agent.Runtime;

public static class ClarificationAnswerFormatter
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    public static string? Format(IReadOnlyList<ClarifyingQuestionAnswerContext> answers)
    {
        if (answers.Count == 0) return null;

        var payload = answers.Select(a => new
        {
            topic = Sanitize(a.Title),
            selected = a.SelectedValue,
            label = a.SelectedLabel,
            category = a.Category,
            scope = a.ConfirmationScope
        });

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("User answers (apply, do not re-ask):");
        sb.AppendLine(JsonSerializer.Serialize(payload, Opts));
        return sb.ToString();
    }

    private static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text.Length > 60 ? text[..60] : text;
    }
}
