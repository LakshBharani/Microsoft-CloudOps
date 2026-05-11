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

    public DiffController(DiffService diffService)
    {
        _diffService = diffService;
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
            if (InfraIntentCompiler.LooksLikeIntent(desiredJson))
                return BadRequest("Intent JSON is no longer accepted here. Send a DesiredStateSpec; intent flows run through the agent and its read tools.");

            desired = desiredJson.Deserialize<DesiredStateSpec>(JsonOpts)
                ?? throw new InvalidOperationException("Desired state JSON is empty.");
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
