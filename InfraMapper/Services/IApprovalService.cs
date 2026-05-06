using InfraMapper.Models;

namespace InfraMapper.Services;

public interface IApprovalService
{
    CreateApprovalResponse CreateApproval(DeploymentManifestRequest manifest, TimeSpan? validity = null);

    bool TryConsume(string approvalId, DeploymentManifestRequest manifest, out string? errorMessage);
}
