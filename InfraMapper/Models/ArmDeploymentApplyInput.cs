namespace InfraMapper.Models;

/// <summary>
/// Input for creating or updating an ARM deployment.
/// Omit <see cref="ResourceGroupName"/> for a subscription-scoped deployment; set it for a resource-group-scoped deployment.
/// </summary>
public sealed class ArmDeploymentApplyInput
{
    public required string SubscriptionId { get; init; }

    /// <summary>When null or whitespace, deployment runs at subscription scope.</summary>
    public string? ResourceGroupName { get; init; }

    public required string DeploymentName { get; init; }

    /// <summary>Full ARM template JSON object.</summary>
    public required string TemplateJson { get; init; }

    /// <summary>ARM parameters object, e.g. <c>{"paramName":{"value":"x"}}</c>. Optional if the template has no parameters.</summary>
    public string? ParametersJson { get; init; }

    /// <summary><c>Incremental</c> (default) or <c>Complete</c>.</summary>
    public string Mode { get; init; } = "Incremental";

    /// <summary>When true, waits until Azure finishes the deployment operation (success or failure at the deployment resource).</summary>
    public bool WaitForCompletion { get; init; } = true;

    /// <summary>Optional Azure region for the deployment resource (subscription-level deployments often set this).</summary>
    public string? Location { get; init; }
}
