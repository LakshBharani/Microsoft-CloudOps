using InfraMapper.Models;
using InfraMapper.Services;
using Microsoft.AspNetCore.Mvc;

namespace InfraMapper.Controllers;

[ApiController]
[Route("api/[controller]")]
public class InfraController : ControllerBase
{
    private readonly AzureResourceService _resourceService;

    public InfraController(AzureResourceService resourceService)
    {
        _resourceService = resourceService;
    }

    [HttpGet("test")]
    public IActionResult Test()
    {
        return Ok("Infra API working");
    }

    /// <summary>Topology only: slim nodes (id, name, type, location, resourceGroup) and edges.</summary>
    [HttpGet("graph")]
    public async Task<ActionResult<InfrastructureGraphSummary>> GetGraph([FromQuery] string? subscriptionId)
    {
        if (!TryResolveSubscriptionId(subscriptionId, out var sub, out var error))
            return BadRequest(error);

        var summary = await _resourceService.GetInfrastructureGraphSummaryAsync(sub);
        return Ok(summary);
    }

    /// <summary>Full inventory: nodes include tags, properties, sku, kind from Resource Graph, plus edges.</summary>
    [HttpGet]
    public async Task<ActionResult<InfrastructureGraph>> GetFullInventory([FromQuery] string? subscriptionId)
    {
        if (!TryResolveSubscriptionId(subscriptionId, out var sub, out var error))
            return BadRequest(error);

        var graph = await _resourceService.GetInfrastructureAsync(sub);
        return Ok(graph);
    }

    private static bool TryResolveSubscriptionId(string? subscriptionId, out string resolved, out string? error)
    {
        error = null;
        resolved = subscriptionId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(resolved))
            resolved = Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(resolved))
        {
            error = "Missing subscriptionId. Provide ?subscriptionId=... or set AZURE_SUBSCRIPTION_ID.";
            return false;
        }

        return true;
    }
}