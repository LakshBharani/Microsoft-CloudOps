namespace InfraMapper.Services.Agent.AgentFramework;

/// <summary>
/// A callable tool for use in agent loops.
/// </summary>
public sealed class AgentTool
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>JSON schema string for the input parameters, or null for no parameters.</summary>
    public string? InputSchema { get; init; }
    /// <summary>Invokes the tool with JSON-encoded arguments. Returns a JSON-encoded result string.</summary>
    public Func<string?, CancellationToken, Task<string>> Invoke { get; init; } = (_, _) => Task.FromResult("{}");
}
