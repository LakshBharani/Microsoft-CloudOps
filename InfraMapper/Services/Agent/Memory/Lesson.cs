namespace InfraMapper.Services.Agent.Memory;

public sealed class Lesson
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public required string IntentSummary { get; init; }
    public required string[] AppliesTo { get; init; }
    public string? WhatFailed { get; init; }
    public required string Recommendation { get; init; }
}
