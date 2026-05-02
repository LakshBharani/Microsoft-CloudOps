namespace InfraMapper.Models;

/// <summary>Minimal node shape for topology (no ARG properties/tags/sku payload).</summary>
public sealed class GraphNodeSummary
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Type { get; init; }

    public required string Location { get; init; }

    public required string ResourceGroup { get; init; }
}
