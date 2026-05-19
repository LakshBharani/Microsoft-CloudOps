using System.Collections.Concurrent;

namespace InfraMapper.Services.Agent;

public sealed class ConversationStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _pendingClarifications = new();
    private readonly ConcurrentDictionary<string, Guid> _pendingApprovedPlans = new();
    public void Touch(string sessionId) =>
        _sessions.AddOrUpdate(sessionId, DateTimeOffset.UtcNow, (_, _) => DateTimeOffset.UtcNow);

    public void Evict(TimeSpan olderThan)
    {
        var cutoff = DateTimeOffset.UtcNow - olderThan;
        foreach (var kv in _sessions)
            if (kv.Value < cutoff)
                _sessions.TryRemove(kv.Key, out _);

        foreach (var kv in _pendingClarifications)
            if (!_sessions.ContainsKey(kv.Key))
                _pendingClarifications.TryRemove(kv.Key, out _);
    }

    public void SetPendingClarification(string sessionId, string answerContext)
    {
        Touch(sessionId);
        _pendingClarifications.AddOrUpdate(sessionId, answerContext, (_, existing) => $"{existing}\n{answerContext}");
    }

    public string? ConsumePendingClarification(string sessionId)
    {
        Touch(sessionId);
        return _pendingClarifications.TryRemove(sessionId, out var answer) ? answer : null;
    }

    public void SetPendingApprovedPlan(string sessionId, Guid planId)
    {
        Touch(sessionId);
        _pendingApprovedPlans[sessionId] = planId;
    }

    public Guid? ConsumePendingApprovedPlan(string sessionId)
    {
        Touch(sessionId);
        return _pendingApprovedPlans.TryRemove(sessionId, out var planId) ? planId : null;
    }

    public void ClearSession(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        _pendingClarifications.TryRemove(sessionId, out _);
        _pendingApprovedPlans.TryRemove(sessionId, out _);
    }

}
