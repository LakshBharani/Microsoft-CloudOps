namespace InfraMapper.Models;

/// <summary>Lightweight graph: node identity + topology edges only.</summary>
public sealed class InfrastructureGraphSummary
{
    public List<GraphNodeSummary> Nodes { get; set; } = new();

    public List<DependencyEdge> Edges { get; set; } = new();
}
