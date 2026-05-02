using InfraMapper.Models;

namespace InfraMapper.Services;

public sealed class InMemoryApprovalService : IApprovalService
{
    private static readonly TimeSpan DefaultValidity = TimeSpan.FromHours(1);

    private readonly object _lock = new();
    private readonly Dictionary<Guid, ApprovalRecord> _records = new();

    public CreateApprovalResponse CreateApproval(DeploymentManifestRequest manifest, TimeSpan? validity = null)
    {
        var input = manifest.ToApplyInput();
        var hash = DeploymentContentHasher.Compute(input);
        var id = Guid.NewGuid();
        var expires = DateTimeOffset.UtcNow.Add(validity ?? DefaultValidity);
        var record = new ApprovalRecord(hash, expires, Consumed: false);

        lock (_lock)
        {
            _records[id] = record;
        }

        return new CreateApprovalResponse { ApprovalId = id, ExpiresAt = expires };
    }

    public bool TryConsume(string approvalId, DeploymentManifestRequest manifest, out string? errorMessage)
    {
        errorMessage = null;
        if (!Guid.TryParse(approvalId, out var id))
        {
            errorMessage = "ApprovalId must be a valid GUID.";
            return false;
        }

        var input = manifest.ToApplyInput();
        var hash = DeploymentContentHasher.Compute(input);

        lock (_lock)
        {
            if (!_records.TryGetValue(id, out var record))
            {
                errorMessage = "Unknown approval id.";
                return false;
            }

            if (record.Consumed)
            {
                errorMessage = "Approval has already been used.";
                return false;
            }

            if (record.ExpiresAt < DateTimeOffset.UtcNow)
            {
                errorMessage = "Approval has expired.";
                return false;
            }

            if (!string.Equals(record.ContentHash, hash, StringComparison.Ordinal))
            {
                errorMessage = "Request does not match the approved deployment manifest (hash mismatch).";
                return false;
            }

            _records[id] = record with { Consumed = true };
            return true;
        }
    }

    private sealed record ApprovalRecord(string ContentHash, DateTimeOffset ExpiresAt, bool Consumed);
}
