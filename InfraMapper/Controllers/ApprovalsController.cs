using InfraMapper.Models;
using InfraMapper.Services;
using Microsoft.AspNetCore.Mvc;

namespace InfraMapper.Controllers;

[ApiController]
[Route("api/infra/approvals")]
public sealed class ApprovalsController : ControllerBase
{
    private readonly IApprovalService _approvalService;

    public ApprovalsController(IApprovalService approvalService)
    {
        _approvalService = approvalService;
    }

    [HttpPost]
    [ProducesResponseType(typeof(CreateApprovalResponse), StatusCodes.Status200OK)]
    public ActionResult<CreateApprovalResponse> Create([FromBody] DeploymentManifestRequest request)
    {
        var response = _approvalService.CreateApproval(request);
        return Ok(response);
    }
}
