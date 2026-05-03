using System.Collections.Concurrent;
using System.Text.Json;

namespace InfraMapper.Services.Agent;

public sealed class QuestionStore
{
    private sealed record QuestionRecord(
        string SessionId,
        JsonElement QuestionData,
        DateTimeOffset ExpiresAt,
        string? Answer = null);

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
        if (!_questions.TryGetValue(questionId, out var r)) { error = "Unknown question."; return false; }
        if (r.ExpiresAt < DateTimeOffset.UtcNow) { error = "Question expired."; return false; }
        _questions[questionId] = r with { Answer = answer };
        return true;
    }

    public JsonElement? GetQuestionData(Guid questionId) =>
        _questions.TryGetValue(questionId, out var r) && r.ExpiresAt > DateTimeOffset.UtcNow
            ? r.QuestionData
            : null;
}
