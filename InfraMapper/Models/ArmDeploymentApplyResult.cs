namespace InfraMapper.Models;

public sealed class ArmDeploymentApplyResult
{
    public bool Succeeded { get; init; }

    public string? DeploymentName { get; init; }

    public string? Scope { get; init; }

    public string? ProvisioningState { get; init; }

    public string? CorrelationId { get; init; }

    public int? HttpStatus { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public string? ErrorDetailJson { get; init; }
}
