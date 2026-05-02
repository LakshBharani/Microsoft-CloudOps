namespace InfraMapper.Models.Agent;

public sealed class AgentChatRequest
{
    public required string Message { get; set; }
    public required string SubscriptionId { get; set; }
    public string? SessionId { get; set; }
}
