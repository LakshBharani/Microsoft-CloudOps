using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfraMapper.Models;
using InfraMapper.Services;
using Microsoft.AspNetCore.Mvc;

namespace InfraMapper.Controllers;

[ApiController]
[Route("api/devops/webhook")]
public class DevOpsWebhookController : ControllerBase
{
    private readonly AzureDevOpsService _ado;
    private readonly DiffService _diff;
    private readonly ILogger<DevOpsWebhookController> _logger;

    public DevOpsWebhookController(
        AzureDevOpsService ado,
        DiffService diff,
        ILogger<DevOpsWebhookController> logger)
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
        [FromQuery] int? prId = null,
        CancellationToken ct = default)
    {
        var body = await new StreamReader(Request.Body).ReadToEndAsync(ct);
        JsonNode? payload;
        try { payload = JsonNode.Parse(body); }
        catch { return BadRequest("Invalid JSON payload."); }

        var eventType = payload?["eventType"]?.GetValue<string>() ?? "";
        _logger.LogInformation("DevOps webhook received: {EventType}", eventType);

        var isWebhook = !string.IsNullOrWhiteSpace(eventType);
        if (isWebhook && !eventType.Contains("pullrequest", StringComparison.OrdinalIgnoreCase))
            return Ok(new { skipped = true, reason = "Not a pull request event." });

        var cfg = new AzureDevOpsConfig
        {
            OrgUrl = orgUrl, Project = project, Repository = repository,
            Pat = pat, Branch = branch, FilePath = filePath
        };

        var payloadPrId = payload?["resource"]?["pullRequestId"]?.GetValue<int>();
        prId ??= payloadPrId;
        var sourceBranch = payload?["resource"]?["sourceRefName"]?.GetValue<string>();
        var targetBranch = payload?["resource"]?["targetRefName"]?.GetValue<string>() ?? branch;

        string? newJson;
        try
        {
            newJson = isWebhook
                ? await _ado.GetDesiredStateAsync(WithBranch(sourceBranch ?? branch), ct)
                : body;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not load desired state: {Error}", ex.Message);
            return Ok(new { skipped = true, reason = "Could not load desired-state.json from repo." });
        }

        if (string.IsNullOrWhiteSpace(newJson))
            return Ok(new { skipped = true, reason = "desired-state.json is empty or missing." });

        string? oldJson = null;
        try { oldJson = await _ado.GetDesiredStateAsync(WithBranch(targetBranch), ct); }
        catch (Exception ex) { _logger.LogInformation("Could not load target desired state: {Error}", ex.Message); }

        DesiredStateSpec newSpec;
        DesiredStateSpec oldSpec;
        try
        {
            newSpec = ParseSpec(newJson);
            oldSpec = string.IsNullOrWhiteSpace(oldJson)
                ? new DesiredStateSpec()
                : ParseSpec(oldJson);
        }
        catch (Exception ex) { return BadRequest($"Could not parse infrastructure JSON: {ex.Message}"); }

        var prDiff = _diff.ComputeDesiredStateDiff(oldSpec, newSpec);
        DiffResult liveDiff;
        try { liveDiff = await _diff.ComputeAsync(subscriptionId, newSpec, ct); }
        catch (Exception ex) { return StatusCode(500, $"Diff failed: {ex.Message}"); }

        var comment = BuildPrComment(prDiff, liveDiff);
        if (prId.HasValue)
        {
            try { await _ado.PostPrCommentAsync(WithBranch(targetBranch), prId.Value, comment, ct); }
            catch (Exception ex) { _logger.LogWarning("Could not post PR comment: {Error}", ex.Message); }
        }

        return Ok(new
        {
            prId,
            intendedCreate = prDiff.ToCreate.Count,
            intendedUpdate = prDiff.ToUpdate.Count,
            intendedDelete = prDiff.ToDelete.Count,
            liveCreate = liveDiff.ToCreate.Count,
            liveUpdate = liveDiff.ToUpdate.Count,
            liveDelete = liveDiff.ToDelete.Count
        });

        AzureDevOpsConfig WithBranch(string? b) => new()
        {
            OrgUrl = cfg.OrgUrl,
            Project = cfg.Project,
            Repository = cfg.Repository,
            Pat = cfg.Pat,
            Branch = string.IsNullOrWhiteSpace(b) ? cfg.Branch : b!,
            FilePath = cfg.FilePath
        };
    }

    private static DesiredStateSpec ParseSpec(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (InfraIntentCompiler.LooksLikeIntent(doc.RootElement))
            throw new InvalidOperationException("Intent JSON is not supported in the webhook diff path. Use the agent chat path; intent flows run through the LLM and its read tools.");

        return doc.RootElement.Deserialize<DesiredStateSpec>(JsonOpts)
            ?? throw new InvalidOperationException("Desired state JSON is empty.");
    }

    private static string BuildPrComment(DiffResult prDiff, DiffResult liveDiff)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## InfraMapper Infrastructure Review");
        sb.AppendLine();

        int total = prDiff.ToCreate.Count + prDiff.ToUpdate.Count + prDiff.ToDelete.Count;
        if (total == 0)
            sb.AppendLine("No desired-state changes detected between target branch and this PR.");
        else
            AppendDiff(sb, $"Desired JSON change ({total})", prDiff);

        var liveTotal = liveDiff.ToCreate.Count + liveDiff.ToUpdate.Count + liveDiff.ToDelete.Count;
        sb.AppendLine();
        if (liveTotal == 0)
            sb.AppendLine("Live Azure already matches the PR desired state.");
        else
            AppendDiff(sb, $"Live Azure drift/apply preview ({liveTotal})", liveDiff);

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("*Merge this PR to queue approved changes for execution via InfraMapper.*");
        return sb.ToString();
    }

    private static void AppendDiff(StringBuilder sb, string title, DiffResult diff)
    {
        sb.AppendLine($"### {title}");
        if (diff.ToCreate.Count > 0)
        {
            sb.AppendLine($"**Create ({diff.ToCreate.Count})**");
            foreach (var n in diff.ToCreate)
                sb.AppendLine($"- `{n.Name}` ({n.Type}) in `{n.ResourceGroup}`");
            sb.AppendLine();
        }

        if (diff.ToUpdate.Count > 0)
        {
            sb.AppendLine($"**Update ({diff.ToUpdate.Count})**");
            foreach (var n in diff.ToUpdate)
            {
                sb.AppendLine($"- `{n.Name}` ({n.Type})");
                foreach (var c in n.Changes)
                    sb.AppendLine($"  - `{c.Field}`: `{c.From}` -> `{c.To}`");
            }
            sb.AppendLine();
        }

        if (diff.ToDelete.Count > 0)
        {
            sb.AppendLine($"**Delete ({diff.ToDelete.Count})**");
            foreach (var n in diff.ToDelete)
                sb.AppendLine($"- `{n.Name}` ({n.Type}) in `{n.ResourceGroup}`");
            sb.AppendLine();
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
}
