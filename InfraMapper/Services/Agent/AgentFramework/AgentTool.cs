namespace InfraMapper.Services.Agent.AgentFramework;

public sealed class AgentTool
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string? InputSchema { get; init; }
    public Func<string?, CancellationToken, Task<string>> Invoke { get; init; } = (_, _) => Task.FromResult("{}");
}
