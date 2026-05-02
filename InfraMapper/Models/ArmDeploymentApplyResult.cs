namespace InfraMapper.Models;

/// <summary>Outcome of an ARM deployment create/update call.</summary>
public sealed class ArmDeploymentApplyResult
{
    public bool Succeeded { get; init; }

    public string? DeploymentName { get; init; }

    /// <summary>ARM scope path used for the deployment (subscription or resource group).</summary>
    public string? Scope { get; init; }

    public string? ProvisioningState { get; init; }

    public string? CorrelationId { get; init; }

    public int? HttpStatus { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    /// <summary>Serialized deployment error from Azure when provisioning failed, if available.</summary>
    public string? ErrorDetailJson { get; init; }
}
