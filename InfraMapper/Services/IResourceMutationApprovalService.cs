using InfraMapper.Models;

namespace InfraMapper.Services;

public interface IResourceMutationApprovalService
{
    CreateApprovalResponse CreateApproval(ResourceMutationManifestRequest manifest, TimeSpan? validity = null);

    bool TryConsume(string approvalId, ResourceMutationManifestRequest manifest, out string? errorMessage);
}
