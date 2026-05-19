using System.ComponentModel;
using InfraMapper.Services.Agent.Tools;
using Microsoft.SemanticKernel;

namespace InfraMapper.Services.Agent.Plugins;

public sealed class AzureExecutePlugin(AzureCrudTools tools)
{
    [KernelFunction("create_or_update_resource")]
    [Description("Create or update one Azure ARM resource by full resource id. Use only after plan is approved.")]
    public Task<string> CreateOrUpdateResourceAsync(
        [Description("Full ARM resource id.")] string resourceId,
        [Description("ARM apiVersion for the resource type.")] string apiVersion,
        [Description("Azure location.")] string location,
        [Description("Resource properties object.")] Dictionary<string, object?>? properties = null,
        [Description("Optional tags.")] Dictionary<string, string>? tags = null,
        [Description("Optional SKU object.")] Dictionary<string, object?>? sku = null,
        [Description("Optional kind value.")] string? kind = null,
        CancellationToken ct = default)
        => tools.CreateOrUpdateResourceAsync(resourceId, apiVersion, location, properties, tags, sku, kind, ct);

    [KernelFunction("delete_resource")]
    [Description("Delete one Azure resource by full ARM resource id. Use only after plan is approved.")]
    public Task<string> DeleteResourceAsync(
        [Description("Full ARM resource id.")] string resource_id,
        CancellationToken ct = default)
        => tools.DeleteResourceAsync(resource_id, ct);

    [KernelFunction("deploy_arm_template")]
    [Description("Validate and deploy an ARM template. Use only after plan is approved.")]
    public Task<string> DeployArmTemplateAsync(
        [Description("Resource group for rg-scoped deployment. Omit for subscription-scoped.")] string? resource_group_name = null,
        [Description("Deployment location for subscription-scoped deployment.")] string? location = null,
        [Description("Deployment name.")] string? deployment_name = null,
        [Description("ARM template object.")] Dictionary<string, object?>? template = null,
        [Description("ARM parameters object.")] Dictionary<string, object?>? parameters = null,
        [Description("Deployment mode: Incremental or Complete.")] string mode = "Incremental",
        CancellationToken ct = default)
        => tools.DeployArmTemplateAsync(
            resourceGroupName: resource_group_name,
            location: location,
            deploymentName: deployment_name,
            template: template,
            parameters: parameters,
            mode: mode,
            cancellationToken: ct);

    [KernelFunction("get_deployment_status")]
    [Description("Get the status of an ARM deployment. Call after deploy_arm_template to verify. Do not call if only create/update/delete was used.")]
    public Task<string> GetDeploymentStatusAsync(
        [Description("Deployment name returned by deploy_arm_template.")] string deployment_name,
        [Description("Optional resource group for rg-scoped deployment.")] string? resource_group_name = null,
        CancellationToken ct = default)
        => tools.GetDeploymentStatusAsync(deployment_name: deployment_name, resource_group_name: resource_group_name, cancellationToken: ct);

    [KernelFunction("get_resource")]
    [Description("Get one Azure resource by full ARM resource id. Use after create/update to verify, or after delete expecting 404.")]
    public Task<string> GetResourceAsync(
        [Description("Full ARM resource id.")] string resource_id,
        CancellationToken ct = default)
        => tools.GetResourceAsync(resource_id, ct);
}
