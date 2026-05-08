using System.Collections.Concurrent;
using InfraMapper.Services.Agent.Runtime;
using InfraMapper.Services.Agent.Tools;
using Microsoft.SemanticKernel.Agents;

namespace InfraMapper.Services.Agent;

public sealed class ConversationStore
{
    public sealed record SessionEntry(ChatCompletionAgent Agent, SkAgentSession Session, DateTimeOffset LastAccessed);

    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();
    private readonly SkAgentFactory _agentFactory;
    private readonly IServiceProvider _services;

    public ConversationStore(SkAgentFactory agentFactory, IServiceProvider services)
    {
        _agentFactory = agentFactory;
        _services = services;
    }

    public Task<SessionEntry> GetOrCreateAsync(string sessionId, string subscriptionId, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(sessionId, out var existing))
        {
            var refreshed = existing with { LastAccessed = DateTimeOffset.UtcNow };
            _sessions[sessionId] = refreshed;
            return Task.FromResult(refreshed);
        }

        var tools = ActivatorUtilities.CreateInstance<AzureCrudTools>(_services, sessionId, subscriptionId);
        var agent = _agentFactory.Create(
            "infra_agent",
            BuildSystemPrompt(subscriptionId),
            (tools, "azure"));

        var entry = new SessionEntry(agent, new SkAgentSession(), DateTimeOffset.UtcNow);
        var stored = _sessions.AddOrUpdate(sessionId, entry, (_, current) => current with { LastAccessed = DateTimeOffset.UtcNow });
        return Task.FromResult(stored);
    }

    public void Evict(TimeSpan olderThan)
    {
        var cutoff = DateTimeOffset.UtcNow - olderThan;
        foreach (var kv in _sessions)
            if (kv.Value.LastAccessed < cutoff)
                _sessions.TryRemove(kv.Key, out _);
    }

    private static string BuildSystemPrompt(string subscriptionId) =>
        $$"""
        You are InfraMapper, a single Azure infrastructure agent for a prototype demo.

        Goal:
        - Read the user's InfraIntentSpec JSON.
        - Inspect Azure before planning.
        - Create a concrete plan.
        - Execute the plan immediately.
        - Verify the deployment/resource result.
        - Stop after success or a clear bounded failure.

        Default subscription_id: {{subscriptionId}}

        Required workflow:
        1. Call list_resources or find_resource for the target resource group/resources.
        2. Call create_plan with operations and deployable template_json or CRUD details.
        3. After create_plan returns, do not wait for user approval. This prototype auto-approves plans.
        4. For multi-resource create/update, call deploy_arm_template.
        5. For one generic resource change, call create_or_update_resource or delete_resource.
        6. Call get_deployment_status or get_resource to verify.
        7. Reply with concise success/failure and exact Azure error if failed.

        JSON contract:
        - Existing InfraIntentSpec is expected: schemaVersion, intent, scope, components, constraints.
        - Supported shortcut components: storageAccount, webApp.
        - Supported generic component: genericResource with type, apiVersion, name, location, properties, optional sku, kindValue, tags.

        Safety:
        - Maximum two execution attempts for the same failed operation.
        - Stop on authorization, quota, invalid template, missing required field, or repeated same error.
        - No lessons, critique, reflection, sub-agents, or self-healing loops.
        """;
}
