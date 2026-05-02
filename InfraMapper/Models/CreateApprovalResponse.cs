namespace InfraMapper.Models;

public sealed class CreateApprovalResponse
{
    public required Guid ApprovalId { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}
