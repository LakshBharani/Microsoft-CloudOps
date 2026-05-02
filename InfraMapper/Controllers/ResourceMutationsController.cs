using InfraMapper.Models;
using InfraMapper.Services;
using Microsoft.AspNetCore.Mvc;

namespace InfraMapper.Controllers;

[ApiController]
[Route("api/infra")]
public sealed class ResourceMutationsController : ControllerBase
{
    private readonly IResourceMutationApprovalService _mutationApprovals;
    private readonly IArmGenericResourceService _genericResources;

    public ResourceMutationsController(
        IResourceMutationApprovalService mutationApprovals,
        IArmGenericResourceService genericResources)
    {
        _mutationApprovals = mutationApprovals;
        _genericResources = genericResources;
    }

    /// <summary>Request approval for a generic resource create/update/delete. Body must match apply exactly.</summary>
    [HttpPost("resource-approvals")]
    [ProducesResponseType(typeof(CreateApprovalResponse), StatusCodes.Status200OK)]
    public ActionResult<CreateApprovalResponse> CreateResourceApproval([FromBody] ResourceMutationManifestRequest request)
    {
        var err = ValidateManifest(request);
        if (err != null)
            return BadRequest(new { error = err });

        var response = _mutationApprovals.CreateApproval(request);
        return Ok(response);
    }

    /// <summary>Apply an approved resource mutation.</summary>
    [HttpPost("resources/apply")]
    [ProducesResponseType(typeof(GenericResourceOperationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<GenericResourceOperationResult>> ApplyResourceMutation(
        [FromBody] ResourceMutationApplyApiRequest request,
        CancellationToken cancellationToken)
    {
        var err = ValidateManifest(request);
        if (err != null)
            return BadRequest(new { error = err });

        if (!_mutationApprovals.TryConsume(request.ApprovalId, request, out var approvalError))
            return BadRequest(new { error = approvalError });

        GenericResourceOperationResult result;
        if (request.Operation == ResourceMutationOperation.Delete)
        {
            result = await _genericResources.DeleteAsync(
                request.ResourceId,
                request.WaitForCompletion,
                cancellationToken);
        }
        else
        {
            result = await _genericResources.CreateOrUpdateAsync(
                request.ResourceId,
                request.Location!,
                request.PropertiesJson,
                request.Tags,
                request.SkuJson,
                request.Kind,
                request.WaitForCompletion,
                cancellationToken);
        }

        return MapResult(result);
    }

    /// <summary>Read a single resource by ARM resource id (no approval).</summary>
    [HttpGet("resources")]
    [ProducesResponseType(typeof(GenericResourceOperationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GenericResourceOperationResult>> GetResource(
        [FromQuery] string id,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest(new { error = "Query parameter id (resource id) is required." });

        var result = await _genericResources.GetAsync(id, cancellationToken);
        return MapResult(result);
    }

    private static string? ValidateManifest(ResourceMutationManifestRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ResourceId))
            return "ResourceId is required.";

        if (request.Operation == ResourceMutationOperation.CreateOrUpdate &&
            string.IsNullOrWhiteSpace(request.Location))
            return "Location is required when Operation is CreateOrUpdate.";

        return null;
    }

    private ActionResult<GenericResourceOperationResult> MapResult(GenericResourceOperationResult result)
    {
        if (result.Succeeded)
            return Ok(result);

        if (result.HttpStatus == StatusCodes.Status404NotFound)
            return NotFound(result);

        if (result.HttpStatus.HasValue && result.HttpStatus >= 400)
            return StatusCode(result.HttpStatus.Value, result);

        return BadRequest(result);
    }
}
