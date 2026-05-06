namespace InfraMapper.Models;

public sealed class GenericResourceOperationResult
{
    public bool Succeeded { get; init; }

    public string? ResourceId { get; init; }

    public int? HttpStatus { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public string? ResourceJson { get; init; }
}
