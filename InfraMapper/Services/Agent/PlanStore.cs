using System.Collections.Concurrent;
using System.Text.Json;

namespace InfraMapper.Services.Agent;

public enum PlanStatus { Pending, Approved, Rejected }

public sealed class PlanStore
{
    private sealed record PlanRecord(
        string SessionId,
        PlanStatus Status,
        JsonElement PlanData,
        DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<Guid, PlanRecord> _plans = new();

    public Guid CreatePlan(string sessionId, JsonElement planData)
    {
        var id = Guid.NewGuid();
        _plans[id] = new PlanRecord(sessionId, PlanStatus.Pending, planData, DateTimeOffset.UtcNow.AddHours(1));
        return id;
    }

    public PlanStatus? GetStatus(Guid planId) =>
        _plans.TryGetValue(planId, out var r) && r.ExpiresAt > DateTimeOffset.UtcNow
            ? r.Status
            : null;

    public bool TryApprove(Guid planId, out string? error)
    {
        error = null;
        if (!_plans.TryGetValue(planId, out var r)) { error = "Unknown plan."; return false; }
        if (r.ExpiresAt < DateTimeOffset.UtcNow) { error = "Plan expired."; return false; }
        if (r.Status == PlanStatus.Rejected) { error = "Plan was already rejected."; return false; }
        _plans[planId] = r with { Status = PlanStatus.Approved };
        return true;
    }

    public bool TryReject(Guid planId, out string? error)
    {
        error = null;
        if (!_plans.TryGetValue(planId, out var r)) { error = "Unknown plan."; return false; }
        _plans[planId] = r with { Status = PlanStatus.Rejected };
        return true;
    }

    public JsonElement? GetPlanData(Guid planId) =>
        _plans.TryGetValue(planId, out var r) && r.ExpiresAt > DateTimeOffset.UtcNow
            ? r.PlanData : null;

    public Guid? GetLatestApprovedForSession(string sessionId)
    {
        var now = DateTimeOffset.UtcNow;
        return _plans
            .Where(kv => kv.Value.SessionId == sessionId
                         && kv.Value.Status == PlanStatus.Approved
                         && kv.Value.ExpiresAt > now)
            .OrderByDescending(kv => kv.Value.ExpiresAt)
            .Select(kv => (Guid?)kv.Key)
            .FirstOrDefault();
    }
}
