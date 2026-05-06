namespace InfraMapper.Models;

public class ResourceNode
{
    public string Id { get; set; }              // Azure resource ID
    public string Name { get; set; }
    public string Type { get; set; }            // e.g. Microsoft.Compute/virtualMachines
    public string Location { get; set; }
    public string ResourceGroup { get; set; }

    public Dictionary<string, string> Tags { get; set; } = new();

    public Dictionary<string, object> Properties { get; set; } = new();

    public string? SkuJson { get; set; }

    public string? Kind { get; set; }
}