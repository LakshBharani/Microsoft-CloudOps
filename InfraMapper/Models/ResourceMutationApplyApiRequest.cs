namespace InfraMapper.Models;

public sealed class ResourceMutationApplyApiRequest : ResourceMutationManifestRequest
{
    public required string ApprovalId { get; set; }
}
