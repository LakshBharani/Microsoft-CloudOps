namespace InfraMapper.Services.Agent.AgentFramework;

/// <summary>
/// Discriminated-union events emitted by <see cref="AnthropicAgent.RunStreamingAsync"/>.
/// </summary>
public abstract record AgentStreamEvent
{
    /// <summary>The orchestrator is about to call a tool.</summary>
    public record ToolCall(string ToolName, string? CallId) : AgentStreamEvent;

    /// <summary>A tool has been executed and returned a result.</summary>
    /// <param name="ResultJson">The raw JSON string returned by the tool.</param>
    public record ToolResult(string ToolName, string? CallId, bool Success, string ResultJson = "") : AgentStreamEvent;

    /// <summary>Fine-grained activity emitted for nested sub-agent work.</summary>
    public record Activity(
        string Phase,
        string Id,
        string? ParentId,
        string Kind,
        string? Agent,
        string? Tool,
        string? Model,
        string Status,
        string Summary,
        string? DetailPreview = null,
        string? ErrorType = null,
        string? Message = null) : AgentStreamEvent;

    /// <summary>Token usage for the current turn.</summary>
    public record Usage(int InputTokens, int OutputTokens) : AgentStreamEvent;

    /// <summary>The agent has finished producing its final response.</summary>
    public record Done(string Text) : AgentStreamEvent;

    /// <summary>An error occurred during the agent loop.</summary>
    public record Error(string Message) : AgentStreamEvent;
}
