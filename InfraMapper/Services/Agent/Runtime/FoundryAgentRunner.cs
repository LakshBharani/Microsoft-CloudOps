using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.Core;
using OpenAI.Responses;

namespace InfraMapper.Services.Agent.Runtime;

#pragma warning disable OPENAI001

public sealed class FoundryAgentRunner : IAgentRunner
{
    private const string DefaultProjectEndpoint = "https://foundry-demo-ms.services.ai.azure.com/api/projects/proj-default";
    private const string DefaultAgentName = "azure-master";
    private const string DefaultAgentVersion = "4";

    private readonly IConfiguration _configuration;
    private readonly TokenCredential _credential;
    private readonly ILogger<FoundryAgentRunner> _logger;
    private readonly CloudOpsMcpAuditStore _auditStore;

    public FoundryAgentRunner(
        IConfiguration configuration,
        TokenCredential credential,
        ILogger<FoundryAgentRunner> logger,
        CloudOpsMcpAuditStore auditStore)
    {
        _configuration = configuration;
        _credential = credential;
        _logger = logger;
        _auditStore = auditStore;
    }

    public async IAsyncEnumerable<AgentStreamEvent> RunStreamingAsync(
        string message,
        string sessionId,
        string subscriptionId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var activityId = $"foundry_{Guid.NewGuid():N}";
        using var auditSubscription = _auditStore.Subscribe(sessionId);
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var events = Channel.CreateUnbounded<AgentStreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var auditPump = Task.Run(async () =>
        {
            try
            {
                await foreach (var auditEvent in auditSubscription.Events.ReadAllAsync(pumpCts.Token))
                {
                    if (auditEvent.Phase == "start")
                    {
                        await events.Writer.WriteAsync(
                            new AgentStreamEvent.ToolCall(auditEvent.Tool, auditEvent.Id),
                            pumpCts.Token);
                    }
                    else
                    {
                        await events.Writer.WriteAsync(
                            new AgentStreamEvent.ToolResult(
                                auditEvent.Tool,
                                auditEvent.Id,
                                auditEvent.Success == true,
                                auditEvent.ResultJson ?? auditEvent.Message ?? ""),
                            pumpCts.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (pumpCts.IsCancellationRequested)
            {
            }
        }, CancellationToken.None);

        _ = Task.Run(async () =>
        {
            ResponseResult? response = null;
            string? errorMessage = null;
            var wasCancelled = false;

            try
            {
                await events.Writer.WriteAsync(new AgentStreamEvent.Activity(
                    "start",
                    activityId,
                    null,
                    "agent",
                    AgentName,
                    null,
                    null,
                    "running",
                    "Calling Azure AI Foundry agent"), ct);

                ct.ThrowIfCancellationRequested();
                var projectClient = new AIProjectClient(new Uri(ProjectEndpoint), _credential);
                var agentReference = new AgentReference(AgentName, AgentVersion);
                ProjectResponsesClient responseClient = projectClient.OpenAI.GetProjectResponsesClientForAgent(agentReference);
                response = await responseClient.CreateResponseAsync(message);
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                wasCancelled = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Foundry agent call failed for session {SessionId}.", sessionId);
                errorMessage = NormalizeError(ex);
            }

            try
            {
                if (wasCancelled)
                {
                    await events.Writer.WriteAsync(new AgentStreamEvent.Activity(
                        "end",
                        activityId,
                        null,
                        "agent",
                        AgentName,
                        null,
                        null,
                        "cancelled",
                        "Foundry agent call cancelled"), CancellationToken.None);
                    return;
                }

                if (errorMessage is not null)
                {
                    await events.Writer.WriteAsync(new AgentStreamEvent.Activity(
                        "end",
                        activityId,
                        null,
                        "agent",
                        AgentName,
                        null,
                        null,
                        "failed",
                        "Foundry agent call failed",
                        Message: errorMessage), CancellationToken.None);
                    await events.Writer.WriteAsync(new AgentStreamEvent.Error(errorMessage), CancellationToken.None);
                    return;
                }

                await Task.Delay(100, CancellationToken.None);
                await events.Writer.WriteAsync(new AgentStreamEvent.Activity(
                    "end",
                    activityId,
                    null,
                    "agent",
                    AgentName,
                    null,
                    null,
                    "success",
                    "Foundry agent call completed"), CancellationToken.None);

                var text = response?.GetOutputText() ?? "";
                if (string.IsNullOrWhiteSpace(text))
                    text = _auditStore.BuildSummary(sessionId) ?? "";

                await events.Writer.WriteAsync(new AgentStreamEvent.Done(text), CancellationToken.None);
            }
            finally
            {
                pumpCts.Cancel();
                try { await auditPump; } catch { }
                events.Writer.TryComplete();
            }
        }, CancellationToken.None);

        await foreach (var evt in events.Reader.ReadAllAsync(ct))
            yield return evt;
    }

    private string ProjectEndpoint =>
        Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
        ?? _configuration["Foundry:ProjectEndpoint"]
        ?? DefaultProjectEndpoint;

    private string AgentName =>
        Environment.GetEnvironmentVariable("FOUNDRY_AGENT_NAME")
        ?? _configuration["Foundry:AgentName"]
        ?? DefaultAgentName;

    private string AgentVersion =>
        Environment.GetEnvironmentVariable("FOUNDRY_AGENT_VERSION")
        ?? _configuration["Foundry:AgentVersion"]
        ?? DefaultAgentVersion;

    private static string NormalizeError(Exception ex)
    {
        var message = ex.Message;
        if (message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("403", StringComparison.OrdinalIgnoreCase))
            return "Azure AI Foundry authorization failed. Verify the app identity can access the Foundry project and agent.";
        if (message.Contains("404", StringComparison.OrdinalIgnoreCase))
            return "Azure AI Foundry agent or project endpoint was not found. Verify Foundry:ProjectEndpoint, Foundry:AgentName, and Foundry:AgentVersion.";
        if (message.Contains("429", StringComparison.OrdinalIgnoreCase))
            return "Azure AI Foundry model rate limit hit (HTTP 429). Wait a minute or raise model quota.";
        return message;
    }
}
