using System.Collections.Concurrent;

namespace InfraMapper.Services.Agent.Runtime;

public sealed class CloudOpsMcpAuditStore
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<CloudOpsMcpAuditEvent>> _events = new();

    public void Record(string sessionId, string tool, bool success, string? message = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var queue = _events.GetOrAdd(sessionId, _ => new ConcurrentQueue<CloudOpsMcpAuditEvent>());
        queue.Enqueue(new CloudOpsMcpAuditEvent(DateTimeOffset.UtcNow, tool, success, message));

        while (queue.Count > 80 && queue.TryDequeue(out _)) { }
    }

    public IReadOnlyList<CloudOpsMcpAuditEvent> Get(string sessionId) =>
        _events.TryGetValue(sessionId, out var queue) ? queue.ToArray() : [];

    public string? BuildSummary(string sessionId)
    {
        var events = Get(sessionId);
        if (events.Count == 0)
            return null;

        var latest = events.TakeLast(12).ToArray();
        var failed = latest.Where(e => !e.Success).ToArray();
        var lines = new List<string>
        {
            failed.Length == 0
                ? "CloudOpsMCP tool calls completed, but Azure AI Foundry returned no final text."
                : "CloudOpsMCP tool calls ran, but one or more calls failed and Azure AI Foundry returned no final text.",
            "",
            "Tool calls:"
        };

        lines.AddRange(latest.Select(e => $"- {e.Tool}: {(e.Success ? "succeeded" : "failed")}{(string.IsNullOrWhiteSpace(e.Message) ? "" : $" ({e.Message})")}"));
        lines.Add("");
        lines.Add("Check the backend terminal for full MCP call logs. If resources were deployed, refresh the graph to verify.");

        return string.Join("\n", lines);
    }
}

public sealed record CloudOpsMcpAuditEvent(
    DateTimeOffset Timestamp,
    string Tool,
    bool Success,
    string? Message);
