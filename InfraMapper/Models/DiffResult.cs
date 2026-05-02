namespace InfraMapper.Models;

public sealed class DiffResult
{
    public List<DiffNode> ToCreate { get; set; } = [];
    public List<DiffNode> ToUpdate { get; set; } = [];
    public List<DiffNode> ToDelete { get; set; } = [];
    public List<DiffNode> Unchanged { get; set; } = [];
}

public sealed class DiffNode
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string ResourceGroup { get; set; } = "";
    public string Location { get; set; } = "";
    public string? ExistingId { get; set; }
    public List<DiffChange> Changes { get; set; } = [];
}

public sealed class DiffChange
{
    public string Field { get; set; } = "";
    public string? From { get; set; }
    public string? To { get; set; }
}
