namespace InfraMapper.Models;

public sealed class ArmDeploymentApplyInput
{
    public required string SubscriptionId { get; init; }

    public string? ResourceGroupName { get; init; }

    public required string DeploymentName { get; init; }

    public required string TemplateJson { get; init; }

    public string? ParametersJson { get; init; }

    public string Mode { get; init; } = "Incremental";

    public bool WaitForCompletion { get; init; } = true;

    public string? Location { get; init; }
}
