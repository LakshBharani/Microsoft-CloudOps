using System.ComponentModel;
using System.Text.Json;
using InfraMapper.Models;
using InfraMapper.Services.Agent.Runtime;

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
    [Description("Ask the user a targeted clarification question when required infrastructure details are missing or ambiguous.")]
    public string AskClarifyingQuestion(
        [Description("Short title for the question.")] string title,
        [Description("Specific prompt explaining what value is needed and why.")] string prompt,
        [Description("Concrete options array. Each item should include label, value, and optional description.")] List<Dictionary<string, object?>>? options = null,
        [Description("Recommended option value if known.")] string? default_value = null,
        [Description("Whether the user may type a custom answer.")] bool allow_custom = true)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(prompt))
            return Error("invalid_question", "title and prompt are required.");

        var normalizedOptions = options ?? [];

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
    [Description("List resources in a subscription or one resource group.")]
    public async Task<string> ListResourcesAsync(
        [Description("Azure subscription id. Uses request subscription when omitted.")] string? subscription_id = null,
        [Description("Optional resource group filter.")] string? resource_group_name = null,
        CancellationToken cancellationToken = default)
    {
        var graph = await _resourceService.GetInfrastructureGraphSummaryAsync(Subscription(subscription_id), resource_group_name, cancellationToken);
        return Ok("resources_listed", new { nodes = graph.Nodes, edges = graph.Edges });
    }
    [Description("Get one Azure resource by full ARM resource id.")]
    public async Task<string> GetResourceAsync(
        [Description("Full ARM resource id.")] string resource_id,
        CancellationToken cancellationToken = default)
    {
        var result = await _genericResources.GetAsync(resource_id, cancellationToken);
        return ResourceResult("resource_read", result);
    }
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
    [Description("Create and auto-approve a plan. Call before any Azure write.")]
    public string CreatePlan(
        [Description("Short title for the plan.")] string title = "Azure infrastructure plan",
        [Description("Required operations array. Each item must include action, resource_type, resource_name, resource_group, details. Never call create_plan without this array.")] List<Dictionary<string, object?>>? operations = null,
        [Description("Risk level: Low, Medium, High.")] string risk_level = "Medium",
        [Description("Optional ARM template object for template deployment.")] Dictionary<string, object?>? template_json = null,
        [Description("Optional ARM parameters object.")] Dictionary<string, object?>? parameters_json = null,
        [Description("Optional resource group for resource-group deployment.")] string? resource_group_name = null,
        [Description("Deployment location for subscription-scope deployment.")] string? location = null,
        [Description("Optional deployment name.")] string? deployment_name = null)
    {
        if (operations is null)
            return Error("invalid_plan", "operations must be a JSON array.");
        if (operations.Count == 0)
            return Error("invalid_plan", "operations must include at least one planned change.");

        var planData = JsonSerializer.SerializeToElement(new
        {
            title,
            operations,
            risk_level,
            template_json,
            parameters_json = parameters_json ?? new Dictionary<string, object?>(),
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
    [Description("Create or update one Azure ARM resource by full resource id and raw ARM fields. Use only after create_plan.")]
    public async Task<string> CreateOrUpdateResourceAsync(
        [Description("Full ARM resource id, e.g. /subscriptions/.../resourceGroups/.../providers/Microsoft.X/type/name.")] string resourceId,
        [Description("ARM apiVersion for the resource type. Required for model planning/audit; Azure SDK infers provider from resourceId.")] string apiVersion,
        [Description("Azure location.")] string location,
        [Description("Resource properties object.")] Dictionary<string, object?>? properties = null,
        [Description("Optional tags.")] Dictionary<string, string>? tags = null,
        [Description("Optional SKU object.")] Dictionary<string, object?>? sku = null,
        [Description("Optional kind value.")] string? kind = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return Error("missing_api_version", "apiVersion is required.");

        var result = await _genericResources.CreateOrUpdateAsync(
            resourceId,
            location,
            JsonOrDefault(properties),
            tags,
            JsonOrNull(sku),
            kind,
            waitForCompletion: true,
            cancellationToken);
        return ResourceResult("resource_mutated", result);
    }
    [Description("Delete one Azure resource. Use only after create_plan.")]
    public async Task<string> DeleteResourceAsync(
        [Description("Full ARM resource id.")] string resource_id,
        CancellationToken cancellationToken = default)
    {
        var result = await _genericResources.DeleteAsync(resource_id, waitForCompletion: true, cancellationToken);
        return ResourceResult("resource_deleted", result);
    }
    [Description("Validate and deploy an ARM template. Use only after create_plan.")]
    public async Task<string> DeployArmTemplateAsync(
        [Description("Azure subscription id. Uses request subscription when omitted.")] string? subscriptionId = null,
        [Description("Resource group for resource-group-scoped deployment. Omit for subscription-scoped deployment.")] string? resourceGroupName = null,
        [Description("Deployment location for subscription-scoped deployment. Also passed through for validation metadata.")] string? location = null,
        [Description("Deployment name.")] string? deploymentName = null,
        [Description("ARM template object.")] Dictionary<string, object?>? template = null,
        [Description("ARM parameters object. Use {} when none.")] Dictionary<string, object?>? parameters = null,
        [Description("Backward-compatible alias for template. Prefer template for new calls.")] Dictionary<string, object?>? template_json = null,
        [Description("Backward-compatible alias for parameters. Prefer parameters for new calls.")] Dictionary<string, object?>? parameters_json = null,
        [Description("Deployment mode: Incremental or Complete.")] string mode = "Incremental",
        CancellationToken cancellationToken = default)
    {
        var templateJson = JsonOrNull(template ?? template_json);
        var parametersJson = JsonOrNull(parameters ?? parameters_json) ?? "{}";
        resourceGroupName = string.IsNullOrWhiteSpace(resourceGroupName) ? null : resourceGroupName;

        if (string.IsNullOrWhiteSpace(templateJson) && TryGetLatestPlanDeploymentDefaults(
                out var planTemplateJson,
                out var planParametersJson,
                out var planResourceGroupName,
                out var planLocation,
                out var planDeploymentName))
        {
            templateJson = planTemplateJson;
            parametersJson = string.IsNullOrWhiteSpace(parametersJson) || parametersJson == "{}"
                ? planParametersJson
                : parametersJson;
            resourceGroupName ??= planResourceGroupName;
            location ??= planLocation;
            deploymentName = string.IsNullOrWhiteSpace(deploymentName) ? planDeploymentName : deploymentName;
        }

        if (string.IsNullOrWhiteSpace(templateJson))
            return Error("missing_template", "template is required. The latest approved plan also does not contain template_json.");

        var input = new ArmDeploymentApplyInput
        {
            SubscriptionId = Subscription(subscriptionId),
            ResourceGroupName = resourceGroupName,
            DeploymentName = string.IsNullOrWhiteSpace(deploymentName) ? $"im-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}" : deploymentName,
            TemplateJson = templateJson,
            ParametersJson = string.IsNullOrWhiteSpace(parametersJson) ? "{}" : parametersJson,
            Mode = mode,
            WaitForCompletion = true,
            Location = location
        };

        var validation = await _deployments.ValidateAsync(input, cancellationToken);
        if (!validation.Succeeded)
            return DeploymentResult(validation);

        var result = await _deployments.CreateOrUpdateAsync(input, cancellationToken);
        return DeploymentResult(result);
    }

    private bool TryGetLatestPlanDeploymentDefaults(
        out string? templateJson,
        out string parametersJson,
        out string? resourceGroupName,
        out string? location,
        out string? deploymentName)
    {
        templateJson = null;
        parametersJson = "{}";
        resourceGroupName = null;
        location = null;
        deploymentName = null;

        var planId = _planStore.GetLatestApprovedForSession(_sessionId);
        if (planId is null)
            return false;

        var planData = _planStore.GetPlanData(planId.Value);
        if (planData is null || planData.Value.ValueKind != JsonValueKind.Object)
            return false;

        var root = planData.Value;
        templateJson = GetRawObject(root, "template_json");
        parametersJson = GetRawObject(root, "parameters_json") ?? "{}";
        resourceGroupName = GetString(root, "resource_group_name");
        location = GetString(root, "location");
        deploymentName = GetString(root, "deployment_name");
        return !string.IsNullOrWhiteSpace(templateJson);
    }
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

    private static string JsonOrDefault(object? value) =>
        JsonOrNull(value) ?? "{}";

    private static string? JsonOrNull(object? value) =>
        value is null ? null : JsonSerializer.Serialize(value, JsonOpts);

    private static string? GetRawObject(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => null
        };
    }

    private static string? GetString(JsonElement root, string name) =>
        TryGetProperty(root, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

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
                    type = Classify(result.HttpStatus, result.ErrorMessage, result.ErrorCode),
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
                    type = Classify(result.HttpStatus, result.ErrorMessage, result.ErrorCode),
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

    private static string Classify(int? status, string? message, string? code = null)
    {
        if (status is 401 or 403) return "authorization";
        if (status == 429) return "quota";
        if (code?.Contains("InvalidTemplate", StringComparison.OrdinalIgnoreCase) == true) return "invalid_template";
        if (message?.Contains("quota", StringComparison.OrdinalIgnoreCase) == true) return "quota";
        if (message?.Contains("InvalidTemplate", StringComparison.OrdinalIgnoreCase) == true) return "invalid_template";
        if (status is >= 400 and < 500) return "bad_request";
        return "azure_api";
    }
}
