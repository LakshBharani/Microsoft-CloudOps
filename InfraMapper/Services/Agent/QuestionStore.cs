using System.Collections.Concurrent;
using System.Text.Json;

namespace InfraMapper.Services.Agent;

public sealed record ClarifyingQuestionAnswerContext(
    Guid QuestionId,
    string SessionId,
    string Title,
    string Prompt,
    string SelectedValue,
    string? SelectedLabel,
    string? SelectedDescription,
    string? DefaultValue,
    string OriginatingAgent,
    string Category,
    string? ConfirmationScope,
    DateTimeOffset AnsweredAt)
{
    public bool IsScopeConfirmation =>
        Category.Equals("scope_confirmation", StringComparison.OrdinalIgnoreCase) ||
        SelectedValue.Contains("test_cleanup", StringComparison.OrdinalIgnoreCase) ||
        SelectedValue.Contains("all_test", StringComparison.OrdinalIgnoreCase) ||
        SelectedValue.Contains("confirm", StringComparison.OrdinalIgnoreCase);
}

public sealed class QuestionStore
{
    private sealed record QuestionRecord(
        string SessionId,
        JsonElement QuestionData,
        DateTimeOffset ExpiresAt,
        string? Answer = null,
        ClarifyingQuestionAnswerContext? AnswerContext = null);

    private readonly ConcurrentDictionary<Guid, QuestionRecord> _questions = new();

    public Guid CreateQuestion(string sessionId, JsonElement questionData)
    {
        var id = Guid.NewGuid();
        _questions[id] = new QuestionRecord(sessionId, questionData, DateTimeOffset.UtcNow.AddHours(1));
        return id;
    }

    public bool TryAnswer(Guid questionId, string answer, out ClarifyingQuestionAnswerContext? context, out string? error)
    {
        context = null;
        error = null;
        if (!_questions.TryGetValue(questionId, out var r)) { error = "Unknown question."; return false; }
        if (r.ExpiresAt < DateTimeOffset.UtcNow) { error = "Question expired."; return false; }
        context = BuildAnswerContext(questionId, r, answer);
        _questions[questionId] = r with { Answer = answer, AnswerContext = context };
        return true;
    }

    public bool TryAnswer(Guid questionId, string answer, out string? error) =>
        TryAnswer(questionId, answer, out _, out error);

    public JsonElement? GetQuestionData(Guid questionId) =>
        _questions.TryGetValue(questionId, out var r) && r.ExpiresAt > DateTimeOffset.UtcNow
            ? r.QuestionData
            : null;

    public ClarifyingQuestionAnswerContext? GetAnswerContext(Guid questionId) =>
        _questions.TryGetValue(questionId, out var r) && r.ExpiresAt > DateTimeOffset.UtcNow
            ? r.AnswerContext
            : null;

    public IReadOnlyList<ClarifyingQuestionAnswerContext> GetAnsweredForSession(string sessionId)
    {
        var now = DateTimeOffset.UtcNow;
        return _questions
            .Where(kv => kv.Value.SessionId == sessionId
                         && kv.Value.AnswerContext is not null
                         && kv.Value.ExpiresAt > now)
            .Select(kv => kv.Value.AnswerContext!)
            .OrderBy(c => c.AnsweredAt)
            .ToList();
    }

    private static ClarifyingQuestionAnswerContext BuildAnswerContext(Guid questionId, QuestionRecord record, string answer)
    {
        var root = record.QuestionData;
        var selected = FindSelectedOption(root, answer);
        var title = GetString(root, "title") ?? "Clarification";
        var prompt = GetString(root, "prompt") ?? "";
        var inferredCategory = InferCategory(title, prompt, answer);
        var storedCategory = GetString(root, "category");
        var category = string.IsNullOrWhiteSpace(storedCategory) ||
                       (storedCategory.Equals("general", StringComparison.OrdinalIgnoreCase) &&
                        !inferredCategory.Equals("general", StringComparison.OrdinalIgnoreCase))
            ? inferredCategory
            : storedCategory;

        return new ClarifyingQuestionAnswerContext(
            questionId,
            record.SessionId,
            title,
            prompt,
            answer,
            selected is null ? null : GetString(selected.Value, "label"),
            selected is null ? null : GetString(selected.Value, "description"),
            GetString(root, "default_value"),
            GetString(root, "originating_agent") ?? "orchestrator",
            category,
            GetString(root, "confirmation_scope"),
            DateTimeOffset.UtcNow);
    }

    private static JsonElement? FindSelectedOption(JsonElement root, string answer)
    {
        if (!TryGetProperty(root, "options", out var options) || options.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var option in options.EnumerateArray())
        {
            if (string.Equals(GetString(option, "value"), answer, StringComparison.OrdinalIgnoreCase))
                return option;
        }

        return null;
    }

    private static string InferCategory(string title, string prompt, string answer)
    {
        var text = $"{title} {prompt} {answer}";
        if (ContainsAny(text, "delete", "destructive", "resource group", "cannot be easily reversed")) return "scope_confirmation";
        if (ContainsAny(text, "exclude", "exclusion", "keep", "preserve")) return "scope_exclusions";
        if (ContainsAny(text, "business reason", "intent", "why")) return "business_reason";
        return "general";
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(n => value.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static string? GetString(JsonElement root, string name) =>
        TryGetProperty(root, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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
