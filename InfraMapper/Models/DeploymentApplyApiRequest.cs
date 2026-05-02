namespace InfraMapper.Models;

public sealed class DeploymentApplyApiRequest : DeploymentManifestRequest
{
    public required string ApprovalId { get; set; }
}
