using InfraMapper.Models;

namespace InfraMapper.Services;

public interface IArmDeploymentService
{
    Task<ArmDeploymentApplyResult> ValidateAsync(ArmDeploymentApplyInput input, CancellationToken cancellationToken = default);

    Task<ArmDeploymentApplyResult> CreateOrUpdateAsync(ArmDeploymentApplyInput input, CancellationToken cancellationToken = default);

    Task<ArmDeploymentApplyResult> GetDeploymentAsync(
        string subscriptionId,
        string? resourceGroupName,
        string deploymentName,
        CancellationToken cancellationToken = default);
}
