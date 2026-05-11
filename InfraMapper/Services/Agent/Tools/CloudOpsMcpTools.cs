using System.ComponentModel;
using InfraMapper.Services.Agent.Runtime;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace InfraMapper.Services.Agent.Tools;

[McpServerToolType]
public static class CloudOpsMcpTools
{
    [McpServerTool(Name = "ask_clarifying_question", ReadOnly = true)]
    [Description("Ask the user a targeted clarification question when required infrastructure details are missing or ambiguous.")]
    public static string AskClarifyingQuestion(
        IServiceProvider services,
        [Description("InfraMapper conversation/session id. Required.")] string session_id,
        [Description("Azure subscription id for this request. Required.")] string subscription_id,
        [Description("Short title for the question.")] string title,
        [Description("Specific prompt explaining what value is needed and why.")] string prompt,
        [Description("Concrete options array. Each item should include label, value, and optional description.")] List<Dictionary<string, object?>>? options = null,
        [Description("Recommended option value if known.")] string? default_value = null,
        [Description("Whether the user may type a custom answer.")] bool allow_custom = true)
    {
        var validation = ValidateContext(session_id, subscription_id);
        return validation ?? Create(services, session_id, subscription_id)
            .AskClarifyingQuestion(title, prompt, options, default_value, allow_custom);
    }

    [McpServerTool(Name = "list_resource_groups", ReadOnly = true)]
    [Description("List resource groups in an Azure subscription.")]
    public static Task<string> ListResourceGroupsAsync(
        IServiceProvider services,
        [Description("InfraMapper conversation/session id. Required.")] string session_id,
        [Description("Azure subscription id for this request. Required.")] string subscription_id,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateContext(session_id, subscription_id);
        return validation is not null
            ? Task.FromResult(validation)
            : Create(services, session_id, subscription_id).ListResourceGroupsAsync(subscription_id, cancellationToken);
    }

    [McpServerTool(Name = "list_resources", ReadOnly = true)]
    [Description("List Azure resources in a subscription or one resource group.")]
    public static Task<string> ListResourcesAsync(
        IServiceProvider services,
        [Description("InfraMapper conversation/session id. Required.")] string session_id,
        [Description("Azure subscription id for this request. Required.")] string subscription_id,
        [Description("Optional resource group filter.")] string? resource_group_name = null,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateContext(session_id, subscription_id);
        return validation is not null
            ? Task.FromResult(validation)
            : Create(services, session_id, subscription_id).ListResourcesAsync(subscription_id, resource_group_name, cancellationToken);
    }

    [McpServerTool(Name = "get_resource", ReadOnly = true)]
    [Description("Get one Azure resource by full ARM resource id.")]
    public static Task<string> GetResourceAsync(
        IServiceProvider services,
        [Description("InfraMapper conversation/session id. Required.")] string session_id,
        [Description("Azure subscription id for this request. Required.")] string subscription_id,
        [Description("Full ARM resource id.")] string resource_id,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateContext(session_id, subscription_id);
        return validation is not null
            ? Task.FromResult(validation)
            : Create(services, session_id, subscription_id).GetResourceAsync(resource_id, cancellationToken);
    }

    [McpServerTool(Name = "find_resource", ReadOnly = true)]
    [Description("Find resources by name and/or type, optionally scoped to one resource group.")]
    public static Task<string> FindResourceAsync(
        IServiceProvider services,
        [Description("InfraMapper conversation/session id. Required.")] string session_id,
        [Description("Azure subscription id for this request. Required.")] string subscription_id,
        [Description("Optional resource group filter.")] string? resource_group_name = null,
        [Description("Optional exact resource name filter.")] string? name = null,
        [Description("Optional exact resource type filter.")] string? type = null,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateContext(session_id, subscription_id);
        return validation is not null
            ? Task.FromResult(validation)
            : Create(services, session_id, subscription_id).FindResourceAsync(subscription_id, resource_group_name, name, type, cancellationToken);
    }

    [McpServerTool(Name = "create_plan", ReadOnly = false, Idempotent = false)]
    [Description("Create and auto-approve an InfraMapper plan. Call before any Azure write.")]
    public static string CreatePlan(
        IServiceProvider services,
        [Description("InfraMapper conversation/session id. Required.")] string session_id,
        [Description("Azure subscription id for this request. Required.")] string subscription_id,
        [Description("Short title for the plan.")] string title = "Azure infrastructure plan",
        [Description("Required operations array. Each item must include action, resource_type, resource_name, resource_group, details.")] List<Dictionary<string, object?>>? operations = null,
        [Description("Risk level: Low, Medium, High.")] string risk_level = "Medium",
        [Description("Optional ARM template object for template deployment.")] Dictionary<string, object?>? template_json = null,
        [Description("Optional ARM parameters object.")] Dictionary<string, object?>? parameters_json = null,
        [Description("Optional resource group for resource-group deployment.")] string? resource_group_name = null,
        [Description("Deployment location for subscription-scope deployment.")] string? location = null,
        [Description("Optional deployment name.")] string? deployment_name = null)
    {
        var validation = ValidateContext(session_id, subscription_id);
        return validation ?? Create(services, session_id, subscription_id)
            .CreatePlan(title, operations, risk_level, template_json, parameters_json, resource_group_name, location, deployment_name);
    }

    [McpServerTool(Name = "create_or_update_resource", ReadOnly = false, Idempotent = true, OpenWorld = true)]
    [Description("Create or update one Azure ARM resource by full resource id and raw ARM fields. Use only after create_plan.")]
    public static Task<string> CreateOrUpdateResourceAsync(
        IServiceProvider services,
        [Description("InfraMapper conversation/session id. Required.")] string session_id,
        [Description("Azure subscription id for this request. Required.")] string subscription_id,
        [Description("Full ARM resource id, e.g. /subscriptions/.../resourceGroups/.../providers/Microsoft.X/type/name.")] string resource_id,
        [Description("ARM apiVersion for the resource type. Required for model planning/audit.")] string api_version,
        [Description("Azure location.")] string location,
        [Description("Resource properties object.")] Dictionary<string, object?>? properties = null,
        [Description("Optional tags.")] Dictionary<string, string>? tags = null,
        [Description("Optional SKU object.")] Dictionary<string, object?>? sku = null,
        [Description("Optional kind value.")] string? kind = null,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateContext(session_id, subscription_id);
        return validation is not null
            ? Task.FromResult(validation)
            : Create(services, session_id, subscription_id).CreateOrUpdateResourceAsync(
                resource_id,
                api_version,
                location,
                properties,
                tags,
                sku,
                kind,
                cancellationToken);
    }

    [McpServerTool(Name = "delete_resource", Destructive = true, ReadOnly = false, Idempotent = true, OpenWorld = true)]
    [Description("Delete one Azure resource. Use only after create_plan.")]
    public static Task<string> DeleteResourceAsync(
        IServiceProvider services,
        [Description("InfraMapper conversation/session id. Required.")] string session_id,
        [Description("Azure subscription id for this request. Required.")] string subscription_id,
        [Description("Full ARM resource id.")] string resource_id,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateContext(session_id, subscription_id);
        return validation is not null
            ? Task.FromResult(validation)
            : Create(services, session_id, subscription_id).DeleteResourceAsync(resource_id, cancellationToken);
    }

    [McpServerTool(Name = "deploy_arm_template", ReadOnly = false, Idempotent = false, OpenWorld = true)]
    [Description("Validate and deploy an ARM template. Use only after create_plan.")]
    public static Task<string> DeployArmTemplateAsync(
        IServiceProvider services,
        [Description("InfraMapper conversation/session id. Required.")] string session_id,
        [Description("Azure subscription id for this request. Required.")] string subscription_id,
        [Description("Resource group for resource-group-scoped deployment. Omit for subscription-scoped deployment.")] string? resource_group_name = null,
        [Description("Deployment location for subscription-scoped deployment. Also passed through for validation metadata.")] string? location = null,
        [Description("Deployment name.")] string? deployment_name = null,
        [Description("ARM template object.")] Dictionary<string, object?>? template = null,
        [Description("ARM parameters object. Use {} when none.")] Dictionary<string, object?>? parameters = null,
        [Description("Deployment mode: Incremental or Complete.")] string mode = "Incremental",
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateContext(session_id, subscription_id);
        return validation is not null
            ? Task.FromResult(validation)
            : Create(services, session_id, subscription_id).DeployArmTemplateAsync(
                subscription_id,
                resource_group_name,
                location,
                deployment_name,
                template,
                parameters,
                template_json: null,
                parameters_json: null,
                mode,
                cancellationToken);
    }

    [McpServerTool(Name = "get_deployment_status", ReadOnly = true)]
    [Description("Get ARM deployment status.")]
    public static Task<string> GetDeploymentStatusAsync(
        IServiceProvider services,
        [Description("InfraMapper conversation/session id. Required.")] string session_id,
        [Description("Azure subscription id for this request. Required.")] string subscription_id,
        [Description("Deployment name.")] string deployment_name,
        [Description("Optional resource group name for resource-group deployment.")] string? resource_group_name = null,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateContext(session_id, subscription_id);
        return validation is not null
            ? Task.FromResult(validation)
            : Create(services, session_id, subscription_id).GetDeploymentStatusAsync(
                subscription_id,
                deployment_name,
                resource_group_name,
                cancellationToken);
    }

    private static AzureCrudTools Create(IServiceProvider services, string sessionId, string subscriptionId) =>
        ActivatorUtilities.CreateInstance<AzureCrudTools>(services, sessionId, subscriptionId);

    private static string? ValidateContext(string sessionId, string subscriptionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Error("missing_session_id", "session_id is required.");
        if (string.IsNullOrWhiteSpace(subscriptionId))
            return Error("missing_subscription_id", "subscription_id is required.");
        return null;
    }

    private static string Error(string type, string message) => AgentResultJson.Serialize(new
    {
        ok = false,
        kind = AgentResultKinds.ToolError,
        error = new { type, message },
        message
    });
}
