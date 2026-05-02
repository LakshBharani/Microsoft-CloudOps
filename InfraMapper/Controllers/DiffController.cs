using InfraMapper.Models;
using InfraMapper.Services;
using Microsoft.AspNetCore.Mvc;

namespace InfraMapper.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DiffController : ControllerBase
{
    private readonly DiffService _diffService;

    public DiffController(DiffService diffService) => _diffService = diffService;

    [HttpPost]
    public async Task<ActionResult<DiffResult>> Diff(
        [FromQuery] string subscriptionId,
        [FromBody] DesiredStateSpec desired,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
            subscriptionId = Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID") ?? "";
        if (string.IsNullOrWhiteSpace(subscriptionId))
            return BadRequest("Missing subscriptionId.");

        var result = await _diffService.ComputeAsync(subscriptionId, desired, ct);
        return Ok(result);
    }
}
