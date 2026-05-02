using System.Text;
using System.Text.Json;
using InfraMapper.Models;
using InfraMapper.Models.Agent;
using InfraMapper.Services;
using InfraMapper.Services.Agent;
using Microsoft.AspNetCore.Mvc;

namespace InfraMapper.Controllers;

/// <summary>
/// Called by the infra-apply pipeline after a PR merges to main.
/// Loads desired-state.json, diffs vs live Azure, and runs the agent
/// with auto-approval to execute the changes.
/// </summary>
[ApiController]
[Route("api/devops/execute")]
public class DevOpsExecuteController : ControllerBase
{
    private readonly AzureDevOpsService _ado;
    private readonly DiffService _diff;
    private readonly AgentService _agent;
    private readonly ILogger<DevOpsExecuteController> _logger;

    public DevOpsExecuteController(
        AzureDevOpsService ado,
        DiffService diff,
        AgentService agent,
        ILogger<DevOpsExecuteController> logger)
    {
        _ado = ado;
        _diff = diff;
        _agent = agent;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Execute(
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
        var cfg = new AzureDevOpsConfig
        {
            OrgUrl = orgUrl, Project = project, Repository = repository,
            Pat = pat, Branch = branch, FilePath = filePath
        };

        // 1. Load desired state from repo
        string? desiredJson;
        try { desiredJson = await _ado.GetDesiredStateAsync(cfg, ct); }
        catch (Exception ex) { return BadRequest($"Could not load desired-state.json: {ex.Message}"); }

        if (string.IsNullOrWhiteSpace(desiredJson))
            return Ok(new { skipped = true, reason = "desired-state.json is empty or missing." });

        DesiredStateSpec spec;
        try { spec = JsonSerializer.Deserialize<DesiredStateSpec>(desiredJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!; }
        catch { return BadRequest("Could not parse desired-state.json."); }

        // 2. Diff vs live Azure
        DiffResult diff;
        try { diff = await _diff.ComputeAsync(subscriptionId, spec, ct); }
        catch (Exception ex) { return StatusCode(500, $"Diff failed: {ex.Message}"); }

        int total = diff.ToCreate.Count + diff.ToUpdate.Count + diff.ToDelete.Count;
        if (total == 0)
        {
            _logger.LogInformation("Execute: no changes detected, skipping.");
            return Ok(new { skipped = true, reason = "No changes detected — live Azure already matches desired state." });
        }

        _logger.LogInformation("Execute: {Total} change(s) to apply.", total);

        // 3. Build prompt from diff and run agent with auto-approve
        var prompt = BuildPrompt(diff);
        var sessionId = Guid.NewGuid().ToString();
        var request = new AgentChatRequest
        {
            Message = prompt,
            SubscriptionId = subscriptionId,
            SessionId = sessionId
        };

        var log = new StringBuilder();
        string? finalReply = null;
        bool succeeded = false;

        try
        {
            await foreach (var evt in _agent.StreamAsync(request, ct, autoApprovePlan: true))
            {
                log.AppendLine(evt);
                var doc = JsonDocument.Parse(evt);
                var type = doc.RootElement.GetProperty("type").GetString();
                if (type == "reply")
                {
                    finalReply = doc.RootElement.GetProperty("data").GetProperty("content").GetString();
                    succeeded = true;
                }
                if (type == "error")
                {
                    finalReply = doc.RootElement.GetProperty("data").GetProperty("message").GetString();
                }
            }
        }
        catch (Exception ex)
        {
            finalReply = ex.Message;
        }

        // 4. Post result as PR comment if prId provided
        if (prId.HasValue)
        {
            var comment = succeeded
                ? $"## InfraMapper Execution Complete\n\n{finalReply}"
                : $"## InfraMapper Execution Failed\n\n{finalReply}";
            try { await _ado.PostPrCommentAsync(cfg, prId.Value, comment, ct); }
            catch (Exception ex) { _logger.LogWarning("Could not post PR comment: {Error}", ex.Message); }
        }

        return Ok(new { succeeded, reply = finalReply, changes = total });
    }

    private static string BuildPrompt(DiffResult diff)
    {
        var lines = new List<string> { "Apply the following infrastructure changes (this was approved via PR):" };
        foreach (var n in diff.ToCreate)
            lines.Add($"- Create {n.Type} \"{n.Name}\" in resource group \"{n.ResourceGroup}\" ({n.Location})");
        foreach (var n in diff.ToUpdate)
        {
            var changes = string.Join(", ", n.Changes.Select(c => $"{c.Field}: {c.From} → {c.To}"));
            lines.Add($"- Update {n.Type} \"{n.Name}\": {changes}");
        }
        foreach (var n in diff.ToDelete)
            lines.Add($"- Delete {n.Type} \"{n.Name}\" (id: {n.ExistingId})");
        return string.Join("\n", lines);
    }
}
