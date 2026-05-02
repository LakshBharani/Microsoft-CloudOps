using InfraMapper.Models;
using InfraMapper.Services;
using Microsoft.AspNetCore.Mvc;

namespace InfraMapper.Controllers;

[ApiController]
[Route("api/infra/deployments")]
public sealed class DeploymentsController : ControllerBase
{
    private readonly IApprovalService _approvalService;
    private readonly IArmDeploymentService _armDeploymentService;

    public DeploymentsController(IApprovalService approvalService, IArmDeploymentService armDeploymentService)
    {
        _approvalService = approvalService;
        _armDeploymentService = armDeploymentService;
    }

    /// <summary>Apply an ARM deployment after approval. Body must match the approved manifest.</summary>
    [HttpPost("apply")]
    [ProducesResponseType(typeof(ArmDeploymentApplyResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ArmDeploymentApplyResult>> Apply(
        [FromBody] DeploymentApplyApiRequest request,
        CancellationToken cancellationToken)
    {
        if (!_approvalService.TryConsume(request.ApprovalId, request, out var approvalError))
            return BadRequest(new { error = approvalError });

        var result = await _armDeploymentService.CreateOrUpdateAsync(request.ToApplyInput(), cancellationToken);
        return Ok(result);
    }

    /// <summary>Read deployment provisioning state (no approval required).</summary>
    [HttpGet("{deploymentName}/status")]
    [ProducesResponseType(typeof(ArmDeploymentApplyResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ArmDeploymentApplyResult>> GetStatus(
        string deploymentName,
        [FromQuery] string subscriptionId,
        [FromQuery] string? resourceGroupName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
            return BadRequest(new { error = "Query parameter subscriptionId is required." });

        var result = await _armDeploymentService.GetDeploymentAsync(
            subscriptionId,
            resourceGroupName,
            deploymentName,
            cancellationToken);

        if (result.HttpStatus == StatusCodes.Status404NotFound)
            return NotFound(result);

        if (!result.Succeeded && result.HttpStatus.HasValue && result.HttpStatus >= 400)
            return StatusCode(result.HttpStatus.Value, result);

        return Ok(result);
    }
}
