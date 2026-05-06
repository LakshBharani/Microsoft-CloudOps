namespace InfraMapper.Models;

public sealed class InfrastructureGraphSummary
{
    public List<GraphNodeSummary> Nodes { get; set; } = new();

    public List<DependencyEdge> Edges { get; set; } = new();
}
