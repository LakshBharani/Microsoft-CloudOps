using InfraMapper.Models;

namespace InfraMapper.Services;

public sealed class InMemoryResourceMutationApprovalService : IResourceMutationApprovalService
{
    private static readonly TimeSpan DefaultValidity = TimeSpan.FromHours(1);

    private readonly object _lock = new();
    private readonly Dictionary<Guid, MutationRecord> _records = new();

    public CreateApprovalResponse CreateApproval(ResourceMutationManifestRequest manifest, TimeSpan? validity = null)
    {
        var hash = ResourceMutationContentHasher.Compute(manifest);
        var id = Guid.NewGuid();
        var expires = DateTimeOffset.UtcNow.Add(validity ?? DefaultValidity);
        var record = new MutationRecord(hash, expires, Consumed: false);

        lock (_lock)
        {
            _records[id] = record;
        }

        return new CreateApprovalResponse { ApprovalId = id, ExpiresAt = expires };
    }

    public bool TryConsume(string approvalId, ResourceMutationManifestRequest manifest, out string? errorMessage)
    {
        errorMessage = null;
        if (!Guid.TryParse(approvalId, out var id))
        {
            errorMessage = "ApprovalId must be a valid GUID.";
            return false;
        }

        var hash = ResourceMutationContentHasher.Compute(manifest);

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
                errorMessage = "Request does not match the approved resource mutation manifest (hash mismatch).";
                return false;
            }

            _records[id] = record with { Consumed = true };
            return true;
        }
    }

    private sealed record MutationRecord(string ContentHash, DateTimeOffset ExpiresAt, bool Consumed);
}
