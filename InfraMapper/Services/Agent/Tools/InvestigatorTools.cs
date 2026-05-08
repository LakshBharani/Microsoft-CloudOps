using System.ComponentModel;
using System.Text.Json;
using Azure;
using InfraMapper.Services;
using Microsoft.SemanticKernel;

namespace InfraMapper.Services.Agent.Tools;

public sealed class InvestigatorTools
{
    private readonly AzureResourceService _resourceService;
    private readonly IArmGenericResourceService _genericResources;
    private readonly ArmExistenceProbe _existenceProbe;

    public InvestigatorTools(
        AzureResourceService resourceService,
        IArmGenericResourceService genericResources,
        ArmExistenceProbe existenceProbe)
    {
        _resourceService = resourceService;
        _genericResources = genericResources;
        _existenceProbe = existenceProbe;
    }

    [KernelFunction("get_infrastructure_graph")]
    [Description("Returns all Azure resources and dependency edges for a subscription. " +
                 "Call this first to understand existing infrastructure before planning changes. " +
                 "If the initial result is insufficient for the investigation focus, call again with a different subscription.")]
    public async Task<string> GetInfrastructureGraphAsync(
        [Description("Azure subscription ID to query")] string subscriptionId,
        [Description("Optional resource group name to scope the query for faster results")] string? resourceGroup = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var graph = await _resourceService.GetInfrastructureGraphSummaryAsync(subscriptionId, resourceGroup, cancellationToken);
            return JsonSerializer.Serialize(graph);
        }
        catch (RequestFailedException ex) when (ex.Status is 429 or 503)
        {
            return JsonSerializer.Serialize(new { error = true, error_type = "transient", message = ex.Message });
        }
        catch (RequestFailedException ex) when (ex.Status == 403)
        {
            return JsonSerializer.Serialize(new { error = true, error_type = "authorization", message = ex.Message });
        }
        catch (RequestFailedException ex)
        {
            return JsonSerializer.Serialize(new { error = true, error_type = "azure_api", message = ex.Message });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = true, error_type = "internal", message = ex.Message });
        }
    }

    [KernelFunction("check_resource_group_exists")]
    [Description("Fast existence check for a single resource group. Returns {exists: bool}. " +
                 "Prefer this over get_infrastructure_graph when you only need to confirm one RG.")]
    public async Task<string> CheckResourceGroupExistsAsync(
        [Description("Azure subscription ID")] string subscriptionId,
        [Description("Resource group name")] string resourceGroupName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId) || string.IsNullOrWhiteSpace(resourceGroupName))
            return JsonSerializer.Serialize(new { error = true, error_type = "validation", message = "subscriptionId and resourceGroupName are required." });

        var resourceId = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}";
        return await ProbeResourceAsync(resourceId, "resource_group", cancellationToken);
    }

    [KernelFunction("check_resource_exists")]
    [Description("Fast existence check for a single Azure resource by full ARM ID. " +
                 "Returns {exists: bool, location, sku, kind} when found, {exists: false} on 404.")]
    public Task<string> CheckResourceExistsAsync(
        [Description("Full ARM resource ID, e.g. /subscriptions/{sub}/resourceGroups/{rg}/providers/{type}/{name}")] string resourceId,
        CancellationToken cancellationToken = default) =>
        ProbeResourceAsync(resourceId, "resource", cancellationToken);

    private async Task<string> ProbeResourceAsync(string resourceId, string kind, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _existenceProbe.ProbeAsync(resourceId, cancellationToken);

            if (result.Exists)
            {
                JsonElement? resourceEl = null;
                if (!string.IsNullOrWhiteSpace(result.ResourceJson))
                {
                    try { resourceEl = JsonSerializer.Deserialize<JsonElement>(result.ResourceJson!); }
                    catch { }
                }
                return JsonSerializer.Serialize(new
                {
                    exists = true,
                    kind,
                    resource_id = resourceId,
                    resource = resourceEl
                });
            }

            if (result.HttpStatus == 404)
                return JsonSerializer.Serialize(new { exists = false, kind, resource_id = resourceId });

            if (result.HttpStatus == 408)
                return JsonSerializer.Serialize(new { error = true, error_type = "transient", message = result.ErrorMessage });

            if (result.HttpStatus is 429 or 503)
                return JsonSerializer.Serialize(new { error = true, error_type = "transient", http_status = result.HttpStatus, message = result.ErrorMessage });

            if (result.HttpStatus == 403)
                return JsonSerializer.Serialize(new { error = true, error_type = "authorization", message = result.ErrorMessage });

            return JsonSerializer.Serialize(new
            {
                error = true,
                error_type = "azure_api",
                http_status = result.HttpStatus,
                error_code = result.ErrorCode,
                message = result.ErrorMessage
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = true, error_type = "internal", message = ex.Message });
        }
    }

    [KernelFunction("get_resource")]
    [Description("Fetches a single Azure resource by its full ARM resource ID. " +
                 "Use this to get detailed properties, SKU, and configuration of a specific resource.")]
    public async Task<string> GetResourceAsync(
        [Description("Full ARM resource ID (e.g. /subscriptions/{sub}/resourceGroups/{rg}/providers/{type}/{name})")] string resourceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _genericResources.GetAsync(resourceId, cancellationToken);
            return JsonSerializer.Serialize(result);
        }
        catch (RequestFailedException ex) when (ex.Status is 429 or 503)
        {
            return JsonSerializer.Serialize(new { error = true, error_type = "transient", message = ex.Message });
        }
        catch (RequestFailedException ex) when (ex.Status == 403)
        {
            return JsonSerializer.Serialize(new { error = true, error_type = "authorization", message = ex.Message });
        }
        catch (RequestFailedException ex)
        {
            return JsonSerializer.Serialize(new { error = true, error_type = "azure_api", message = ex.Message });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = true, error_type = "internal", message = ex.Message });
        }
    }
}
