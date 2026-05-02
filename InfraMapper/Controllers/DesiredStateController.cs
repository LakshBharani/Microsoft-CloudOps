using System.Text.Json;
using InfraMapper.Models;
using InfraMapper.Services;
using Microsoft.AspNetCore.Mvc;

namespace InfraMapper.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DesiredStateController : ControllerBase
{
    private readonly AzureDevOpsService _ado;
    private readonly DiffService _diff;

    public DesiredStateController(AzureDevOpsService ado, DiffService diff)
    {
        _ado = ado;
        _diff = diff;
    }

    /// <summary>Load desired-state.json from the Azure DevOps repo.</summary>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string orgUrl, [FromQuery] string project,
        [FromQuery] string repository, [FromQuery] string pat,
        [FromQuery] string branch = "main", [FromQuery] string filePath = "infra/desired-state.json",
        CancellationToken ct = default)
    {
        var cfg = new AzureDevOpsConfig { OrgUrl = orgUrl, Project = project, Repository = repository, Pat = pat, Branch = branch, FilePath = filePath };
        try
        {
            var content = await _ado.GetDesiredStateAsync(cfg, ct);
            if (content == null) return NotFound("File not found in repository.");
            return Content(content, "application/json");
        }
        catch (Exception ex) { return BadRequest(ex.Message); }
    }

    /// <summary>Save desired-state.json to the Azure DevOps repo verbatim.</summary>
    [HttpPut]
    public async Task<IActionResult> Put([FromBody] SaveDesiredStateRequest req, CancellationToken ct)
    {
        var cfg = new AzureDevOpsConfig
        {
            OrgUrl = req.OrgUrl, Project = req.Project, Repository = req.Repository,
            Pat = req.Pat, Branch = req.Branch, FilePath = req.FilePath
        };
        try
        {
            await _ado.PushDesiredStateAsync(cfg, req.RawJson, req.CommitMessage ?? "Update desired infrastructure state", ct);
            return Ok(new { committed = true });
        }
        catch (Exception ex) { return BadRequest(ex.Message); }
    }
}

public sealed class SaveDesiredStateRequest
{
    public string OrgUrl { get; set; } = "";
    public string Project { get; set; } = "";
    public string Repository { get; set; } = "";
    public string Pat { get; set; } = "";
    public string Branch { get; set; } = "main";
    public string FilePath { get; set; } = "infra/desired-state.json";
    public string? CommitMessage { get; set; }
    public string RawJson { get; set; } = "{}";
}
