namespace InfraMapper.Models.Agent;

public sealed class AgentCallEvent
{
    public required string Agent { get; init; }
    public required string Model { get; init; }
    public int Iteration { get; init; }
    public string? ParentToolCallId { get; init; }
    public required string SessionId { get; init; }
}

public sealed class AgentResultEvent
{
    public required string Agent { get; init; }
    public bool Success { get; init; }
    public int Iteration { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public required string SessionId { get; init; }
}
