namespace InfraMapper.Models;

public class InfrastructureGraph
{
    public List<ResourceNode> Nodes { get; set; } = new();
    public List<DependencyEdge> Edges { get; set; } = new();
}