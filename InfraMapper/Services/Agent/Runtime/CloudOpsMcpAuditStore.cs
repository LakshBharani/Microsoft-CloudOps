using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace InfraMapper.Services.Agent.Runtime;

public sealed class CloudOpsMcpAuditStore
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<CloudOpsMcpAuditEvent>> _events = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<CloudOpsMcpAuditEvent>>> _subscribers = new();

    public string RecordStart(string sessionId, string tool)
    {
        var callId = $"{tool}_{Guid.NewGuid():N}";
        Record(new CloudOpsMcpAuditEvent(sessionId, callId, "start", DateTimeOffset.UtcNow, tool, null, null, null));
        return callId;
    }

    public void RecordResult(string sessionId, string tool, string resultJson, string? callId = null)
    {
        var success = AgentResultParser.IsSuccessfulToolResult(resultJson);
        Record(sessionId, tool, success, ExtractMessage(resultJson), resultJson, callId);
    }

    public void Record(string sessionId, string tool, bool success, string? message = null, string? resultJson = null, string? callId = null)
    {
        Record(new CloudOpsMcpAuditEvent(
            sessionId,
            callId ?? $"{tool}_{Guid.NewGuid():N}",
            "end",
            DateTimeOffset.UtcNow,
            tool,
            success,
            message,
            resultJson));
    }

    private void Record(CloudOpsMcpAuditEvent evt)
    {
        var sessionId = evt.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var queue = _events.GetOrAdd(sessionId, _ => new ConcurrentQueue<CloudOpsMcpAuditEvent>());
        queue.Enqueue(evt);

        while (queue.Count > 80 && queue.TryDequeue(out _)) { }

        if (_subscribers.TryGetValue(sessionId, out var subscribers))
        {
            foreach (var subscriber in subscribers.Values)
                subscriber.Writer.TryWrite(evt);
        }
    }

    public IReadOnlyList<CloudOpsMcpAuditEvent> Get(string sessionId) =>
        _events.TryGetValue(sessionId, out var queue) ? queue.ToArray() : [];

    public CloudOpsMcpAuditSubscription Subscribe(string sessionId)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<CloudOpsMcpAuditEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        var subscribers = _subscribers.GetOrAdd(sessionId, _ => new ConcurrentDictionary<Guid, Channel<CloudOpsMcpAuditEvent>>());
        subscribers[id] = channel;
        return new CloudOpsMcpAuditSubscription(id, sessionId, channel, this);
    }

    private void Unsubscribe(string sessionId, Guid id)
    {
        if (!_subscribers.TryGetValue(sessionId, out var subscribers))
            return;

        if (subscribers.TryRemove(id, out var channel))
            channel.Writer.TryComplete();

        if (subscribers.IsEmpty)
            _subscribers.TryRemove(sessionId, out _);
    }

    public string? BuildSummary(string sessionId)
    {
        var events = Get(sessionId);
        var completed = events.Where(e => e.Phase == "end").ToArray();
        if (completed.Length == 0)
            return null;

        var implementedNames = TrySummarizeImplementedResources(completed);
        if (!string.IsNullOrWhiteSpace(implementedNames) &&
            completed.Any(e => e.Tool == "list_resources" && e.Success == true))
        {
            return $"Successfully implemented {implementedNames}. Verification completed.";
        }

        var latest = completed.TakeLast(12).ToArray();
        var failed = latest.Where(e => e.Success == false).ToArray();
        var lines = new List<string>
        {
            failed.Length == 0
                ? "CloudOpsMCP tool calls completed, but Azure AI Foundry returned no final text."
                : "CloudOpsMCP tool calls ran, but one or more calls failed and Azure AI Foundry returned no final text.",
            "",
            "Tool calls:"
        };

        lines.AddRange(latest.Select(e => $"- {e.Tool}: {(e.Success == true ? "succeeded" : "failed")}{(string.IsNullOrWhiteSpace(e.Message) ? "" : $" ({e.Message})")}"));
        lines.Add("");
        lines.Add("Check the backend terminal for full MCP call logs. If resources were deployed, refresh the graph to verify.");

        return string.Join("\n", lines);
    }

    private static string? TrySummarizeImplementedResources(IReadOnlyList<CloudOpsMcpAuditEvent> events)
    {
        var hasSuccessfulWrite = events.Any(e =>
            e.Success == true &&
            (e.Tool == "create_or_update_resource" || e.Tool == "delete_resource" || e.Tool == "deploy_arm_template"));
        if (!hasSuccessfulWrite)
            return null;

        var plan = events.LastOrDefault(e => e.Tool == "create_plan" && e.Success == true && !string.IsNullOrWhiteSpace(e.ResultJson));
        if (plan?.ResultJson is null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(plan.ResultJson);
            if (!TryGetProperty(doc.RootElement, "data", out var data) ||
                !TryGetProperty(data, "operations", out var operations) ||
                operations.ValueKind != JsonValueKind.Array)
                return null;

            var names = operations.EnumerateArray()
                .Select(FormatOperationTarget)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Cast<string>()
                .ToArray();

            return names.Length == 0 ? null : string.Join(", ", names);
        }
        catch
        {
            return null;
        }
    }

    private static string? FormatOperationTarget(JsonElement op)
    {
        var type = TryGetProperty(op, "resource_type", out var typeEl) ? typeEl.GetString() : null;
        var name = TryGetProperty(op, "resource_name", out var nameEl) ? nameEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return $"{FriendlyResourceType(type)} {name}";
    }

    private static string FriendlyResourceType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return "resource";

        return type.Split('/').LastOrDefault()?.ToLowerInvariant() switch
        {
            "storageaccounts" => "storage account",
            "virtualnetworks" => "virtual network",
            "networksecuritygroups" => "network security group",
            "publicipaddresses" => "public IP address",
            "networkinterfaces" => "network interface",
            "virtualmachines" => "virtual machine",
            "appservices" => "app service",
            "serverfarms" => "app service plan",
            _ => type.Split('/').Last().Replace("-", " ").ToLowerInvariant()
        };
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ExtractMessage(string result)
    {
        if (!AgentResultParser.TryParse(result, out var parsed))
            return null;
        return parsed.Message ?? parsed.Kind ?? parsed.ErrorType;
    }

    public sealed class CloudOpsMcpAuditSubscription : IDisposable
    {
        private readonly Guid _id;
        private readonly string _sessionId;
        private readonly CloudOpsMcpAuditStore _owner;

        public CloudOpsMcpAuditSubscription(
            Guid id,
            string sessionId,
            Channel<CloudOpsMcpAuditEvent> channel,
            CloudOpsMcpAuditStore owner)
        {
            _id = id;
            _sessionId = sessionId;
            _owner = owner;
            Events = channel.Reader;
        }

        public ChannelReader<CloudOpsMcpAuditEvent> Events { get; }

        public void Dispose() => _owner.Unsubscribe(_sessionId, _id);
    }
}

public sealed record CloudOpsMcpAuditEvent(
    string SessionId,
    string Id,
    string Phase,
    DateTimeOffset Timestamp,
    string Tool,
    bool? Success,
    string? Message,
    string? ResultJson);
