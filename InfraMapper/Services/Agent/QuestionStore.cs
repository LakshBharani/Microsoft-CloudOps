using System.Collections.Concurrent;
using System.Text.Json;

namespace InfraMapper.Services.Agent;

public sealed class QuestionStore
{
    private sealed record QuestionRecord(string SessionId, JsonElement QuestionData, DateTimeOffset ExpiresAt, string? Answer = null);

    private readonly ConcurrentDictionary<Guid, QuestionRecord> _questions = new();

    public Guid CreateQuestion(string sessionId, JsonElement questionData)
    {
        var id = Guid.NewGuid();
        _questions[id] = new QuestionRecord(sessionId, questionData, DateTimeOffset.UtcNow.AddHours(1));
        return id;
    }

    public bool TryAnswer(Guid questionId, string answer, out string? error)
    {
        error = null;
        if (!_questions.TryGetValue(questionId, out var record))
        {
            error = "Unknown question.";
            return false;
        }

        if (record.ExpiresAt < DateTimeOffset.UtcNow)
        {
            error = "Question expired.";
            return false;
        }

        _questions[questionId] = record with { Answer = answer };
        return true;
    }

    public string FormatAnswer(Guid questionId, string answer)
    {
        if (!_questions.TryGetValue(questionId, out var record))
            return $"The user answered clarification question {questionId}: {answer}.";

        var title = GetString(record.QuestionData, "title") ?? "Clarification";
        var prompt = GetString(record.QuestionData, "prompt") ?? "";
        var selected = FindSelectedOption(record.QuestionData, answer);
        var label = selected is null ? answer : GetString(selected.Value, "label") ?? answer;
        var description = selected is null ? null : GetString(selected.Value, "description");

        return JsonSerializer.Serialize(new
        {
            clarification_answer = new
            {
                question_id = questionId,
                title,
                prompt,
                answer,
                selected_label = label,
                selected_description = description
            }
        });
    }

    private static JsonElement? FindSelectedOption(JsonElement root, string answer)
    {
        if (!TryGetProperty(root, "options", out var options) || options.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var option in options.EnumerateArray())
            if (string.Equals(GetString(option, "value"), answer, StringComparison.OrdinalIgnoreCase))
                return option;

        return null;
    }

    private static string? GetString(JsonElement root, string name) =>
        TryGetProperty(root, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
