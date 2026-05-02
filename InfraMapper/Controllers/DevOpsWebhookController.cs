using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfraMapper.Models;
using InfraMapper.Services;
using Microsoft.AspNetCore.Mvc;

namespace InfraMapper.Controllers;

/// <summary>
/// Receives Azure DevOps service hook events (pull request created/updated).
/// When desired-state.json changes, runs a diff against live Azure and posts
/// the result as a PR comment.
/// </summary>
[ApiController]
[Route("api/devops/webhook")]
public class DevOpsWebhookController : ControllerBase
{
    private readonly AzureDevOpsService _ado;
    private readonly DiffService _diff;
    private readonly ILogger<DevOpsWebhookController> _logger;

    public DevOpsWebhookController(AzureDevOpsService ado, DiffService diff, ILogger<DevOpsWebhookController> logger)
    {
        _ado = ado;
        _diff = diff;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Receive(
        [FromQuery] string subscriptionId,
        [FromQuery] string orgUrl,
        [FromQuery] string project,
        [FromQuery] string repository,
        [FromQuery] string pat,
        [FromQuery] string branch = "main",
        [FromQuery] string filePath = "infra/desired-state.json",
        CancellationToken ct = default)
    {
        var body = await new StreamReader(Request.Body).ReadToEndAsync(ct);
        JsonNode? payload;
        try { payload = JsonNode.Parse(body); }
        catch { return BadRequest("Invalid JSON payload."); }

        var eventType = payload?["eventType"]?.GetValue<string>() ?? "";
        _logger.LogInformation("DevOps webhook received: {EventType}", eventType);

        // Only handle PR created / updated events
        if (!eventType.Contains("pullrequest", StringComparison.OrdinalIgnoreCase))
            return Ok(new { skipped = true, reason = "Not a pull request event." });

        var prId = payload?["resource"]?["pullRequestId"]?.GetValue<int>();
        if (prId == null)
            return Ok(new { skipped = true, reason = "No pull request ID in payload." });

        var cfg = new AzureDevOpsConfig
        {
            OrgUrl = orgUrl, Project = project, Repository = repository,
            Pat = pat, Branch = branch, FilePath = filePath
        };

        // Load desired state from the PR source branch
        string? desiredJson;
        try { desiredJson = await _ado.GetDesiredStateAsync(cfg, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not load desired state: {Error}", ex.Message);
            return Ok(new { skipped = true, reason = "Could not load desired-state.json from repo." });
        }

        if (string.IsNullOrWhiteSpace(desiredJson))
            return Ok(new { skipped = true, reason = "desired-state.json is empty or missing." });

        DesiredStateSpec spec;
        try { spec = JsonSerializer.Deserialize<DesiredStateSpec>(desiredJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!; }
        catch { return BadRequest("Could not parse desired-state.json."); }

        DiffResult diff;
        try { diff = await _diff.ComputeAsync(subscriptionId, spec, ct); }
        catch (Exception ex) { return StatusCode(500, $"Diff failed: {ex.Message}"); }

        var comment = BuildPrComment(diff);
        try { await _ado.PostPrCommentAsync(cfg, prId.Value, comment, ct); }
        catch (Exception ex) { _logger.LogWarning("Could not post PR comment: {Error}", ex.Message); }

        return Ok(new
        {
            prId,
            toCreate = diff.ToCreate.Count,
            toUpdate = diff.ToUpdate.Count,
            toDelete = diff.ToDelete.Count,
            unchanged = diff.Unchanged.Count
        });
    }

    private static string BuildPrComment(DiffResult diff)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## InfraMapper Diff");
        sb.AppendLine();

        int total = diff.ToCreate.Count + diff.ToUpdate.Count + diff.ToDelete.Count;
        if (total == 0)
        {
            sb.AppendLine("No infrastructure changes detected. Live Azure matches the desired state.");
            return sb.ToString();
        }

        sb.AppendLine($"**{total} change(s)** detected between `desired-state.json` and live Azure:");
        sb.AppendLine();

        if (diff.ToCreate.Count > 0)
        {
            sb.AppendLine($"### Create ({diff.ToCreate.Count})");
            foreach (var n in diff.ToCreate)
                sb.AppendLine($"- `{n.Name}` ({n.Type}) in `{n.ResourceGroup}`");
            sb.AppendLine();
        }

        if (diff.ToUpdate.Count > 0)
        {
            sb.AppendLine($"### Update ({diff.ToUpdate.Count})");
            foreach (var n in diff.ToUpdate)
            {
                sb.AppendLine($"- `{n.Name}` ({n.Type})");
                foreach (var c in n.Changes)
                    sb.AppendLine($"  - `{c.Field}`: `{c.From}` → `{c.To}`");
            }
            sb.AppendLine();
        }

        if (diff.ToDelete.Count > 0)
        {
            sb.AppendLine($"### Delete ({diff.ToDelete.Count})");
            foreach (var n in diff.ToDelete)
                sb.AppendLine($"- `{n.Name}` ({n.Type}) in `{n.ResourceGroup}`");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine("*Approve this PR to queue the changes for execution via InfraMapper Agent.*");
        return sb.ToString();
    }
}
