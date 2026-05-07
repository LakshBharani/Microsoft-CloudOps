namespace InfraMapper.Services.Agent.Runtime;

public abstract record AgentStreamEvent
{
    public record ToolCall(string ToolName, string? CallId) : AgentStreamEvent;

    /// <param name="ResultJson">The raw JSON string returned by the tool.</param>
    public record ToolResult(string ToolName, string? CallId, bool Success, string ResultJson = "") : AgentStreamEvent;

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

    public record Usage(int InputTokens, int OutputTokens) : AgentStreamEvent;

    public record Done(string Text) : AgentStreamEvent;

    public record Error(string Message) : AgentStreamEvent;
}
