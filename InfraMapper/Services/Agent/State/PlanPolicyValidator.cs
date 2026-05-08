using InfraMapper.Services.Agent.Tools;

namespace InfraMapper.Services.Agent.State;

public static class PlanPolicyValidator
{
    private static readonly string[] DisallowedComputeTypes =
    {
        "Microsoft.Compute/virtualMachines",
        "Microsoft.Compute/virtualMachineScaleSets",
        "Microsoft.ContainerService/managedClusters",
        "Microsoft.Web/serverfarms",
        "Microsoft.Web/sites",
        "Microsoft.DBforPostgreSQL/servers",
        "Microsoft.DBforPostgreSQL/flexibleServers",
        "Microsoft.DBforMySQL/servers",
        "Microsoft.DBforMySQL/flexibleServers",
        "Microsoft.Sql/servers",
        "Microsoft.Sql/servers/databases",
        "Microsoft.Network/publicIPAddresses",
        "Microsoft.DocumentDB/databaseAccounts",
        "Microsoft.Cache/Redis"
    };

    public static ValidatorError? Validate(AgentTaskState? state, PlanOperationDto[] operations)
    {
        if (state is null) return null;
        if (!state.StudentSafe && !state.NoCompute) return null;

        var blocked = operations
            .Where(o => !string.Equals(o.Action, "Delete", StringComparison.OrdinalIgnoreCase))
            .Where(o => DisallowedComputeTypes.Any(t => string.Equals(t, o.ResourceType, StringComparison.OrdinalIgnoreCase)))
            .Select(o => $"{o.ResourceType} \"{o.ResourceName}\"")
            .Distinct()
            .ToArray();

        if (blocked.Length == 0) return null;

        return new ValidatorError(
            "policy_violation_no_compute",
            $"Plan violates student-safe/no-compute policy. Disallowed resources: {string.Join(", ", blocked)}. Replace with allowed resources (storage, networking metadata, key vault, tags) or remove them.",
            new
            {
                disallowed = blocked,
                policy = state.NoCompute ? "no-compute" : "student-safe"
            });
    }
}
