using System.Text.Json;

namespace InfraMapper.Services.Agent;

public enum PlanStatus { Pending, Approved, Rejected }

public sealed class PlanStore
{
    private readonly object _lock = new();
    private Guid? _id;
    private string? _sessionId;
    private PlanStatus _status;
    private JsonElement _data;

    public Guid CreatePlan(string sessionId, JsonElement planData)
    {
        var id = Guid.NewGuid();
        lock (_lock)
        {
            _id = id;
            _sessionId = sessionId;
            _status = PlanStatus.Pending;
            _data = planData;
        }
        return id;
    }

    public PlanStatus? GetStatus(Guid planId)
    {
        lock (_lock) return _id == planId ? _status : null;
    }

    public bool TryApprove(Guid planId, out string? error)
    {
        lock (_lock)
        {
            if (_id != planId) { error = "Unknown plan."; return false; }
            if (_status == PlanStatus.Rejected) { error = "Plan was already rejected."; return false; }
            _status = PlanStatus.Approved;
            error = null;
            return true;
        }
    }

    public bool TryReject(Guid planId, out string? error)
    {
        lock (_lock)
        {
            if (_id != planId) { error = "Unknown plan."; return false; }
            _status = PlanStatus.Rejected;
            error = null;
            return true;
        }
    }

    public JsonElement? GetPlanData(Guid planId)
    {
        lock (_lock) return _id == planId ? _data : null;
    }

    public Guid? GetLatestApprovedForSession(string sessionId)
    {
        lock (_lock)
            return _sessionId == sessionId && _status == PlanStatus.Approved ? _id : null;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _id = null;
            _sessionId = null;
            _data = default;
        }
    }
}
