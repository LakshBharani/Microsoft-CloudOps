namespace InfraMapper.Services.Agent.Runtime;

public interface IAgentRunner
{
    IAsyncEnumerable<AgentStreamEvent> RunStreamingAsync(
        string message,
        string sessionId,
        string subscriptionId,
        CancellationToken ct);
}
