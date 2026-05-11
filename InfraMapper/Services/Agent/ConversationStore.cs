using System.Collections.Concurrent;

namespace InfraMapper.Services.Agent;

public sealed class ConversationStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _pendingClarifications = new();

    public void Touch(string sessionId) =>
        _sessions.AddOrUpdate(sessionId, DateTimeOffset.UtcNow, (_, _) => DateTimeOffset.UtcNow);

    public void Evict(TimeSpan olderThan)
    {
        var cutoff = DateTimeOffset.UtcNow - olderThan;
        foreach (var kv in _sessions)
            if (kv.Value < cutoff)
                _sessions.TryRemove(kv.Key, out _);

        foreach (var kv in _pendingClarifications)
            if (!_sessions.ContainsKey(kv.Key))
                _pendingClarifications.TryRemove(kv.Key, out _);
    }

    public void SetPendingClarification(string sessionId, string answerContext)
    {
        Touch(sessionId);
        _pendingClarifications.AddOrUpdate(sessionId, answerContext, (_, existing) => $"{existing}\n{answerContext}");
    }

    public string? ConsumePendingClarification(string sessionId)
    {
        Touch(sessionId);
        return _pendingClarifications.TryRemove(sessionId, out var answer) ? answer : null;
    }

    internal static string BuildSystemPrompt(string subscriptionId, string sessionId) =>
        $$"""
        You are the Azure infrastructure agent for InfraMapper.

        Use CloudOps MCP tools for complete Azure CRUD, ARM generation, validation, what-if/deployment, plans, questions, and verification.
        Use Microsoft Azure MCP tools only for supported Azure-native inspection or service-specific helper operations.

        Required CloudOps MCP context for every CloudOps tool call:
        - session_id: {{sessionId}}
        - subscription_id: {{subscriptionId}}

        Required workflow:
        1. Inspect Azure first with CloudOps list_resource_groups, list_resources, or find_resource - if the any resource group does not exist, first create that resource group as a step in the plan and include it in the ARM template as a nested deployment.
        2. Create a concrete plan with create_plan before any Azure write.
        3. After create_plan returns, continue immediately; prototype mode auto-approves plans.
        4. Prefer deploy_arm_template for related or multi-resource work.
        5. Use create_or_update_resource only for simple single-resource CRUD.
        6. Use delete_resource only after a plan explicitly includes that delete.
        7. Verify with get_deployment_status, get_resource, or list_resources.
        8. Reply with concise success/failure and exact Azure error details if failed.

        ARM generation:
        - Generate real ARM resource definitions dynamically from Azure resource type, apiVersion, name, sku, kind, properties, dependsOn, and tags.
        - Do not use hardcoded shortcut component builders.
        - Do not use a generic fake resource abstraction.
        - For unknown Azure resource types, use ARM knowledge to construct valid resource type/apiVersion/properties.
        - Ask a clarifying question if required fields are missing.
        - For subnet child resources, fully qualify names as vnetName/subnetName.
        - For storage accounts, enforce lowercase alphanumeric names, 3-24 chars.
        - For low-cost storage, use Standard_LRS and StorageV2.

        Safety:
        - Never invent compute resources when noCompute is true.
        - If noCompute is true, do not create Microsoft.Compute/*, Microsoft.Web/sites, Microsoft.ContainerService/*, Microsoft.Sql/*, Microsoft.DBfor*, or Microsoft.Network/publicIPAddresses.
        - Stop on authorization, quota, invalid template, missing required field, or repeated same error.
        """;
}
