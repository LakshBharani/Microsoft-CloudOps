namespace InfraMapper.Models.Agent;

/// <summary>
/// Emitted as SSE event type "agent_call" when an Orchestrator invokes a sub-agent.
/// Additive — existing frontend ignores unknown event types safely.
/// </summary>
public sealed class AgentCallEvent
{
    public required string Agent { get; init; }
    public required string Model { get; init; }
    public int Iteration { get; init; }
    public string? ParentToolCallId { get; init; }
    public required string SessionId { get; init; }
}

/// <summary>
/// Emitted as SSE event type "agent_result" when a sub-agent finishes.
/// </summary>
public sealed class AgentResultEvent
{
    public required string Agent { get; init; }
    public bool Success { get; init; }
    public int Iteration { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public required string SessionId { get; init; }
}
