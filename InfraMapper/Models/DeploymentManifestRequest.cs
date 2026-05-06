namespace InfraMapper.Models;

public class DeploymentManifestRequest
{
    public required string SubscriptionId { get; set; }

    public string? ResourceGroupName { get; set; }

    public required string DeploymentName { get; set; }

    public required string TemplateJson { get; set; }

    public string? ParametersJson { get; set; }

    public string Mode { get; set; } = "Incremental";

    public bool WaitForCompletion { get; set; } = true;

    public string? Location { get; set; }

    public ArmDeploymentApplyInput ToApplyInput() => new()
    {
        SubscriptionId = SubscriptionId,
        ResourceGroupName = ResourceGroupName,
        DeploymentName = DeploymentName,
        TemplateJson = TemplateJson,
        ParametersJson = ParametersJson,
        Mode = Mode,
        WaitForCompletion = WaitForCompletion,
        Location = Location
    };
}
