using System.ComponentModel;

namespace InfraMapper.Services.Agent.Tools;

public record PlanOperationDto(
    [property: Description("Action to take: Create, Update, Delete, or Deploy")] string Action,
    [property: Description("Azure resource type, e.g. Microsoft.Storage/storageAccounts")] string ResourceType,
    [property: Description("Resource name")] string ResourceName,
    [property: Description("Resource group")] string? ResourceGroup = null,
    [property: Description("Additional details about the operation")] string? Details = null);
