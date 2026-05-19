namespace InfraMapper.Models;

public sealed class ArmWhatIfChange
{
    public string? ResourceId { get; set; }
    public string? ChangeType { get; set; }
    public string? UnsupportedReason { get; set; }
    public IReadOnlyList<ArmWhatIfPropertyChange>? Delta { get; set; }
}

public sealed class ArmWhatIfPropertyChange
{
    public string? Path { get; set; }
    public string? PropertyChangeType { get; set; }
    public object? Before { get; set; }
    public object? After { get; set; }
}

public sealed class ArmWhatIfSummary
{
    public int Create { get; set; }
    public int Modify { get; set; }
    public int Delete { get; set; }
    public int NoChange { get; set; }
    public int Ignore { get; set; }
    public int Deploy { get; set; }
    public int Unsupported { get; set; }
}

public sealed class ArmWhatIfResult
{
    public bool Succeeded { get; set; }
    public string? Scope { get; set; }
    public string? DeploymentName { get; set; }
    public IReadOnlyList<ArmWhatIfChange> Changes { get; set; } = Array.Empty<ArmWhatIfChange>();
    public ArmWhatIfSummary Summary { get; set; } = new();
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
