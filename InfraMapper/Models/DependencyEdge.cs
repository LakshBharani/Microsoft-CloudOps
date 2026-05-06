namespace InfraMapper.Models;

public class DependencyEdge
{
    public string SourceId { get; set; }   // e.g. VM
    public string TargetId { get; set; }   // e.g. NIC

    public string DependencyType { get; set; }
    // Examples: "network", "compute", "data", "identity"

    public double RiskWeight { get; set; }
}