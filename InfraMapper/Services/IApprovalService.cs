using InfraMapper.Models;

namespace InfraMapper.Services;

public interface IApprovalService
{
    /// <summary>Registers an approval for the exact manifest; returns id and expiry.</summary>
    CreateApprovalResponse CreateApproval(DeploymentManifestRequest manifest, TimeSpan? validity = null);

    /// <summary>Validates id, expiry, hash match, and single use; marks consumed if valid.</summary>
    bool TryConsume(string approvalId, DeploymentManifestRequest manifest, out string? errorMessage);
}
