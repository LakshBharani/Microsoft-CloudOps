namespace InfraMapper.Models;

public class ResourceNode
{
    public string Id { get; set; }              // Azure resource ID
    public string Name { get; set; }
    public string Type { get; set; }            // e.g. Microsoft.Compute/virtualMachines
    public string Location { get; set; }
    public string ResourceGroup { get; set; }

    public Dictionary<string, string> Tags { get; set; } = new();

    /// <summary>Resource-specific payload from ARM (nested values deserialize as JsonElement).</summary>
    public Dictionary<string, object> Properties { get; set; } = new();

    /// <summary>Raw JSON from Resource Graph <c>sku</c> when present.</summary>
    public string? SkuJson { get; set; }

    /// <summary>Resource Graph <c>kind</c> when present (e.g. StorageV2).</summary>
    public string? Kind { get; set; }
}