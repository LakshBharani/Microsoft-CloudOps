using System.ComponentModel;
using InfraMapper.Services.Agent.Tools;
using Microsoft.SemanticKernel;

namespace InfraMapper.Services.Agent.Plugins;

public sealed class AzureReadPlugin(AzureCrudTools tools)
{
    [KernelFunction("list_resource_groups")]
    [Description("List all Azure resource groups in the subscription.")]
    public Task<string> ListResourceGroupsAsync(CancellationToken ct = default)
        => tools.ListResourceGroupsAsync(cancellationToken: ct);

    [KernelFunction("list_resources")]
    [Description("List resources in a subscription or one resource group.")]
    public Task<string> ListResourcesAsync(
        [Description("Optional resource group filter.")] string? resource_group_name = null,
        CancellationToken ct = default)
        => tools.ListResourcesAsync(resource_group_name: resource_group_name, cancellationToken: ct);

    [KernelFunction("get_resource")]
    [Description("Get one Azure resource by full ARM resource id.")]
    public Task<string> GetResourceAsync(
        [Description("Full ARM resource id.")] string resource_id,
        CancellationToken ct = default)
        => tools.GetResourceAsync(resource_id, ct);

    [KernelFunction("find_resource")]
    [Description("Find resources by name and/or type, optionally scoped to one resource group.")]
    public Task<string> FindResourceAsync(
        [Description("Optional resource group filter.")] string? resource_group_name = null,
        [Description("Optional exact resource name filter.")] string? name = null,
        [Description("Optional exact resource type filter.")] string? type = null,
        CancellationToken ct = default)
        => tools.FindResourceAsync(resource_group_name: resource_group_name, name: name, type: type, cancellationToken: ct);

    [KernelFunction("trace_dependencies")]
    [Description("Trace dependency edges of one Azure resource. Read-only. Call before planning any write that touches networked resources.")]
    public Task<string> TraceDependenciesAsync(
        [Description("Full ARM resource id to trace from.")] string resource_id,
        [Description("BFS depth limit 1-5. Default 2.")] int depth = 2,
        [Description("Edge direction: 'out', 'in', or 'both'.")] string direction = "both",
        CancellationToken ct = default)
        => tools.TraceDependenciesAsync(resource_id, depth: depth, direction: direction, cancellationToken: ct);
}
