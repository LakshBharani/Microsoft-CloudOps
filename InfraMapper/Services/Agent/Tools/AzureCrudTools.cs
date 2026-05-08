using System.ComponentModel;
using System.Text.Json;
using InfraMapper.Models;
using InfraMapper.Services.Agent.Runtime;
using Microsoft.SemanticKernel;

namespace InfraMapper.Services.Agent.Tools;

public sealed class AzureCrudTools
{
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly AzureResourceService _resourceService;
    private readonly IArmGenericResourceService _genericResources;
    private readonly IArmDeploymentService _deployments;
    private readonly PlanStore _planStore;
    private readonly QuestionStore _questionStore;
    private readonly string _sessionId;
    private readonly string _defaultSubscriptionId;

    public AzureCrudTools(
        AzureResourceService resourceService,
        IArmGenericResourceService genericResources,
        IArmDeploymentService deployments,
        PlanStore planStore,
        QuestionStore questionStore,
        string sessionId,
        string defaultSubscriptionId)
    {
        _resourceService = resourceService;
        _genericResources = genericResources;
        _deployments = deployments;
        _planStore = planStore;
        _questionStore = questionStore;
        _sessionId = sessionId;
        _defaultSubscriptionId = defaultSubscriptionId;
    }

    [KernelFunction("ask_clarifying_question")]
    [Description("Ask the user a targeted clarification question when required infrastructure details are missing or ambiguous.")]
    public string AskClarifyingQuestion(
        [Description("Short title for the question.")] string title,
        [Description("Specific prompt explaining what value is needed and why.")] string prompt,
        [Description("Concrete options as JSON array. Each item should include label, value, and optional description.")] JsonElement options,
        [Description("Recommended option value if known.")] string? default_value = null,
        [Description("Whether the user may type a custom answer.")] bool allow_custom = true)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(prompt))
            return Error("invalid_question", "title and prompt are required.");

        var normalizedOptions = options.ValueKind == JsonValueKind.Array
            ? JsonSerializer.Deserialize<object[]>(options.GetRawText()) ?? []
            : Array.Empty<object>();

        var questionData = JsonSerializer.SerializeToElement(new
        {
            title,
            prompt,
            options = normalizedOptions,
            default_value,
            allow_custom,
            category = "general",
            originating_agent = "infra_agent"
        }, JsonOpts);
        var questionId = _questionStore.CreateQuestion(_sessionId, questionData);

        return AgentResultJson.Serialize(new
        {
            ok = true,
            kind = AgentResultKinds.ClarificationRequired,
            question = new
            {
                question_id = questionId,
                title,
                prompt,
                options = normalizedOptions,
                default_value,
                allow_custom,
                category = "general",
                originating_agent = "infra_agent"
            },
            message = "Clarification required before continuing."
        });
    }

    [KernelFunction("list_resource_groups")]
    [Description("List resource groups in the subscription.")]
    public async Task<string> ListResourceGroupsAsync(
        [Description("Azure subscription id. Uses request subscription when omitted.")] string? subscription_id = null,
        CancellationToken cancellationToken = default)
    {
        var graph = await _resourceService.GetInfrastructureGraphSummaryAsync(Subscription(subscription_id), null, cancellationToken);
        var groups = graph.Nodes
            .Where(n => string.Equals(n.Type, "Microsoft.Resources/resourceGroups", StringComparison.OrdinalIgnoreCase))
            .Select(n => new { n.Name, n.Id, n.Location })
            .OrderBy(n => n.Name)
            .ToArray();

        return Ok("resource_groups", new { resource_groups = groups });
    }

    [KernelFunction("list_resources")]
    [Description("List resources in a subscription or one resource group.")]
    public async Task<string> ListResourcesAsync(
        [Description("Azure subscription id. Uses request subscription when omitted.")] string? subscription_id = null,
        [Description("Optional resource group filter.")] string? resource_group_name = null,
        CancellationToken cancellationToken = default)
    {
        var graph = await _resourceService.GetInfrastructureGraphSummaryAsync(Subscription(subscription_id), resource_group_name, cancellationToken);
        return Ok("resources_listed", new { nodes = graph.Nodes, edges = graph.Edges });
    }

    [KernelFunction("get_resource")]
    [Description("Get one Azure resource by full ARM resource id.")]
    public async Task<string> GetResourceAsync(
        [Description("Full ARM resource id.")] string resource_id,
        CancellationToken cancellationToken = default)
    {
        var result = await _genericResources.GetAsync(resource_id, cancellationToken);
        return ResourceResult("resource_read", result);
    }

    [KernelFunction("find_resource")]
    [Description("Find resources by name and/or type, optionally scoped to one resource group.")]
    public async Task<string> FindResourceAsync(
        [Description("Azure subscription id. Uses request subscription when omitted.")] string? subscription_id = null,
        [Description("Optional resource group filter.")] string? resource_group_name = null,
        [Description("Optional exact resource name filter.")] string? name = null,
        [Description("Optional exact resource type filter.")] string? type = null,
        CancellationToken cancellationToken = default)
    {
        var graph = await _resourceService.GetInfrastructureGraphSummaryAsync(Subscription(subscription_id), resource_group_name, cancellationToken);
        var matches = graph.Nodes.Where(n =>
            (string.IsNullOrWhiteSpace(name) || string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(type) || string.Equals(n.Type, type, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        return Ok("resources_found", new { matches });
    }

    [KernelFunction("create_plan")]
    [Description("Create and auto-approve a plan. Call before any Azure write.")]
    public string CreatePlan(
        [Description("Short title for the plan.")] string title,
        [Description("Operations array. Each item should include action, resource_type, resource_name, resource_group, details.")] JsonElement operations,
        [Description("Risk level: Low, Medium, High.")] string risk_level = "Medium",
        [Description("Optional ARM template JSON for template deployment.")] JsonElement template_json = default,
        [Description("Optional ARM parameters JSON.")] JsonElement parameters_json = default,
        [Description("Optional resource group for resource-group deployment.")] string? resource_group_name = null,
        [Description("Deployment location for subscription-scope deployment.")] string? location = null,
        [Description("Optional deployment name.")] string? deployment_name = null)
    {
        if (operations.ValueKind != JsonValueKind.Array)
            return Error("invalid_plan", "operations must be a JSON array.");

        var planData = JsonSerializer.SerializeToElement(new
        {
            title,
            operations = JsonSerializer.Deserialize<object[]>(operations.GetRawText()) ?? [],
            risk_level,
            template_json = NormalizeJson(template_json),
            parameters_json = NormalizeJson(parameters_json) ?? "{}",
            resource_group_name,
            location,
            deployment_name = string.IsNullOrWhiteSpace(deployment_name) ? $"im-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}" : deployment_name
        }, JsonOpts);

        var planId = _planStore.CreatePlan(_sessionId, planData);
        _planStore.TryApprove(planId, out _);

        return AgentResultJson.Serialize(new
        {
            ok = true,
            kind = AgentResultKinds.PlanCreated,
            plan_id = planId,
            status = "auto_approved",
            data = planData,
            message = "Plan created and auto-approved for prototype mode. Execute it now."
        });
    }

    [KernelFunction("create_or_update_resource")]
    [Description("Create or update one Azure resource. Use only after create_plan.")]
    public async Task<string> CreateOrUpdateResourceAsync(
        [Description("Full ARM resource id.")] string resource_id,
        [Description("Azure location.")] string location,
        [Description("Resource properties JSON.")] string? properties_json = "{}",
        [Description("Optional tags.")] Dictionary<string, string>? tags = null,
        [Description("Optional SKU JSON.")] string? sku_json = null,
        [Description("Optional kind value.")] string? kind = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _genericResources.CreateOrUpdateAsync(
            resource_id,
            location,
            properties_json,
            tags,
            sku_json,
            kind,
            waitForCompletion: true,
            cancellationToken);
        return ResourceResult("resource_mutated", result);
    }

    [KernelFunction("delete_resource")]
    [Description("Delete one Azure resource. Use only after create_plan.")]
    public async Task<string> DeleteResourceAsync(
        [Description("Full ARM resource id.")] string resource_id,
        CancellationToken cancellationToken = default)
    {
        var result = await _genericResources.DeleteAsync(resource_id, waitForCompletion: true, cancellationToken);
        return ResourceResult("resource_deleted", result);
    }

    [KernelFunction("deploy_arm_template")]
    [Description("Deploy an ARM template. Use only after create_plan.")]
    public async Task<string> DeployArmTemplateAsync(
        [Description("Azure subscription id. Uses request subscription when omitted.")] string? subscription_id = null,
        [Description("Deployment name.")] string? deployment_name = null,
        [Description("ARM template JSON.")] string template_json = "",
        [Description("ARM parameters JSON. Use {} when none.")] string? parameters_json = "{}",
        [Description("Resource group for resource-group-scoped deployment. Omit for subscription-scoped deployment.")] string? resource_group_name = null,
        [Description("Deployment location for subscription-scoped deployment.")] string? location = null,
        [Description("Deployment mode: Incremental or Complete.")] string mode = "Incremental",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(template_json))
            return Error("missing_template", "template_json is required.");

        var input = new ArmDeploymentApplyInput
        {
            SubscriptionId = Subscription(subscription_id),
            ResourceGroupName = string.IsNullOrWhiteSpace(resource_group_name) ? null : resource_group_name,
            DeploymentName = string.IsNullOrWhiteSpace(deployment_name) ? $"im-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}" : deployment_name,
            TemplateJson = template_json,
            ParametersJson = string.IsNullOrWhiteSpace(parameters_json) ? "{}" : parameters_json,
            Mode = mode,
            WaitForCompletion = true,
            Location = location
        };

        var result = await _deployments.CreateOrUpdateAsync(input, cancellationToken);
        return DeploymentResult(result);
    }

    [KernelFunction("get_deployment_status")]
    [Description("Get ARM deployment status.")]
    public async Task<string> GetDeploymentStatusAsync(
        [Description("Azure subscription id. Uses request subscription when omitted.")] string? subscription_id = null,
        [Description("Deployment name.")] string deployment_name = "",
        [Description("Optional resource group name for resource-group deployment.")] string? resource_group_name = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deployment_name))
            return Error("missing_deployment_name", "deployment_name is required.");

        var result = await _deployments.GetDeploymentAsync(
            Subscription(subscription_id),
            resource_group_name,
            deployment_name,
            cancellationToken);
        return DeploymentResult(result);
    }

    private string Subscription(string? subscriptionId) =>
        string.IsNullOrWhiteSpace(subscriptionId) ? _defaultSubscriptionId : subscriptionId;

    private static string? NormalizeJson(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            _ => element.GetRawText()
        };

    private static string Ok(string kind, object data) => AgentResultJson.Serialize(new
    {
        ok = true,
        kind,
        data
    });

    private static string Error(string type, string message) => AgentResultJson.Serialize(new
    {
        ok = false,
        kind = AgentResultKinds.ToolError,
        error = new { type, message },
        message
    });

    private static string ResourceResult(string kind, GenericResourceOperationResult result)
    {
        if (!result.Succeeded)
            return AgentResultJson.Serialize(new
            {
                ok = false,
                kind = AgentResultKinds.ToolError,
                error = new
                {
                    type = Classify(result.HttpStatus, result.ErrorMessage),
                    message = result.ErrorMessage,
                    code = result.ErrorCode,
                    http_status = result.HttpStatus
                },
                message = result.ErrorMessage
            });

        return AgentResultJson.Serialize(new
        {
            ok = true,
            kind,
            data = new
            {
                resource_id = result.ResourceId,
                resource_json = TryParseJson(result.ResourceJson)
            }
        });
    }

    private static string DeploymentResult(ArmDeploymentApplyResult result)
    {
        if (!result.Succeeded)
            return AgentResultJson.Serialize(new
            {
                ok = false,
                kind = AgentResultKinds.DeploymentFailed,
                error = new
                {
                    type = Classify(result.HttpStatus, result.ErrorMessage),
                    message = result.ErrorMessage,
                    code = result.ErrorCode,
                    http_status = result.HttpStatus
                },
                data = result,
                message = result.ErrorMessage
            });

        return AgentResultJson.Serialize(new
        {
            ok = true,
            kind = AgentResultKinds.DeploymentSucceeded,
            data = result,
            message = $"Deployment {result.DeploymentName} succeeded."
        });
    }

    private static object? TryParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(json); }
        catch { return json; }
    }

    private static string Classify(int? status, string? message)
    {
        if (status is 401 or 403) return "authorization";
        if (status == 429) return "quota";
        if (message?.Contains("quota", StringComparison.OrdinalIgnoreCase) == true) return "quota";
        if (message?.Contains("InvalidTemplate", StringComparison.OrdinalIgnoreCase) == true) return "invalid_template";
        if (status is >= 400 and < 500) return "bad_request";
        return "azure_api";
    }
}
