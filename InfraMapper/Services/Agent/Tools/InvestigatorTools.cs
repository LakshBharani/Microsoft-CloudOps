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

    public InvestigatorTools(
        AzureResourceService resourceService,
        IArmGenericResourceService genericResources)
    {
        _resourceService = resourceService;
        _genericResources = genericResources;
    }

    [KernelFunction("get_infrastructure_graph")]
    [Description("Returns all Azure resources and dependency edges for a subscription. " +
                 "Call this first to understand existing infrastructure before planning changes. " +
                 "If the initial result is insufficient for the investigation focus, call again with a different subscription.")]
    public async Task<string> GetInfrastructureGraphAsync(
        [Description("Azure subscription ID to query")] string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var graph = await _resourceService.GetInfrastructureGraphSummaryAsync(subscriptionId);
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
