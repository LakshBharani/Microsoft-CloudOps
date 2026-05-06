using InfraMapper.Models;
using InfraMapper.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace InfraMapper.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DiffController : ControllerBase
{
    private readonly DiffService _diffService;
    private readonly InfraIntentCompiler _intentCompiler;

    public DiffController(DiffService diffService, InfraIntentCompiler intentCompiler)
    {
        _diffService = diffService;
        _intentCompiler = intentCompiler;
    }

    [HttpPost]
    public async Task<ActionResult<DiffResult>> Diff(
        [FromQuery] string subscriptionId,
        [FromBody] JsonElement desiredJson,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
            subscriptionId = Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID") ?? "";
        if (string.IsNullOrWhiteSpace(subscriptionId))
            return BadRequest("Missing subscriptionId.");

        DesiredStateSpec desired;
        try
        {
            desired = InfraIntentCompiler.LooksLikeIntent(desiredJson)
                ? _intentCompiler.Compile(desiredJson.Deserialize<InfraIntentSpec>(JsonOpts)!, subscriptionId).DesiredState
                : desiredJson.Deserialize<DesiredStateSpec>(JsonOpts)!;
        }
        catch (Exception ex)
        {
            return BadRequest($"Could not parse infrastructure JSON: {ex.Message}");
        }

        var result = await _diffService.ComputeAsync(subscriptionId, desired, ct);
        return Ok(result);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
}
