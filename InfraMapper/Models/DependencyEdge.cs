namespace InfraMapper.Models;

public class DependencyEdge
{
    public string SourceId { get; set; }   // e.g. VM
    public string TargetId { get; set; }   // e.g. NIC

    public string DependencyType { get; set; }
    // Examples: "network", "compute", "data", "identity"

    /// <summary>Relative risk contribution for this edge’s dependency class (0–1 scale).</summary>
    public double RiskWeight { get; set; }
}