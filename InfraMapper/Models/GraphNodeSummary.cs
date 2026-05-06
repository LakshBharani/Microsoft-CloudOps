namespace InfraMapper.Models;

public sealed class GraphNodeSummary
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Type { get; init; }

    public required string Location { get; init; }

    public required string ResourceGroup { get; init; }
}
