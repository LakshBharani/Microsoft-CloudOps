using System.Collections.Concurrent;

namespace InfraMapper.Services.Agent;

public sealed class ConversationStore
{
    private sealed record SessionEntry(List<MessageParam> Messages, DateTimeOffset LastAccessed);

    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();

    public List<MessageParam> GetOrCreate(string sessionId)
    {
        var entry = _sessions.AddOrUpdate(
            sessionId,
            _ => new SessionEntry(new List<MessageParam>(), DateTimeOffset.UtcNow),
            (_, existing) => existing with { LastAccessed = DateTimeOffset.UtcNow });
        return entry.Messages;
    }

    public void Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);

    public void Evict(TimeSpan maxIdle)
    {
        var cutoff = DateTimeOffset.UtcNow - maxIdle;
        foreach (var key in _sessions.Keys)
            if (_sessions.TryGetValue(key, out var entry) && entry.LastAccessed < cutoff)
                _sessions.TryRemove(key, out _);
    }
}
