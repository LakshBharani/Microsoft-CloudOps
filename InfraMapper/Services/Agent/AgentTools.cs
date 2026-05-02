using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using InfraMapper.Models;

namespace InfraMapper.Services.Agent;

public sealed class AgentTools
{
    private readonly AzureResourceService _resourceService;
    private readonly IArmDeploymentService _deploymentService;
    private readonly IApprovalService _approvalService;
    private readonly IResourceMutationApprovalService _mutationApprovals;
    private readonly IArmGenericResourceService _genericResources;
    private readonly PlanStore _planStore;
    private readonly ILogger<AgentTools> _logger;

    public AgentTools(
        AzureResourceService resourceService,
        IArmDeploymentService deploymentService,
        IApprovalService approvalService,
        IResourceMutationApprovalService mutationApprovals,
        IArmGenericResourceService genericResources,
        PlanStore planStore,
        ILogger<AgentTools> logger)
    {
        _resourceService = resourceService;
        _deploymentService = deploymentService;
        _approvalService = approvalService;
        _mutationApprovals = mutationApprovals;
        _genericResources = genericResources;
        _planStore = planStore;
        _logger = logger;
    }

    public IReadOnlyList<ToolDefinition> Definitions { get; } = BuildDefinitions();

    public async Task<string> ExecuteAsync(string toolName, JsonElement input, string subscriptionId, string sessionId, CancellationToken ct)
    {
        _logger.LogInformation("Agent tool call: {ToolName} | input: {Input}", toolName, input.GetRawText());
        try
        {
            return toolName switch
            {
                "get_infrastructure_graph" => await GetInfrastructureGraphAsync(input, subscriptionId, ct),
                "get_resource" => await GetResourceAsync(input, ct),
                "get_deployment_status" => await GetDeploymentStatusAsync(input, ct),
                "create_plan" => CreatePlan(input, sessionId),
                "deploy_arm_template" => await DeployArmTemplateAsync(input, ct),
                "apply_resource_mutation" => await ApplyResourceMutationAsync(input, ct),
                _ => $"Unknown tool: {toolName}"
            };
        }
        catch (RequestFailedException ex) when (ex.Status is 429 or 503)
        {
            return JsonSerializer.Serialize(new
            {
                error = true,
                error_type = "transient",
                message = ex.Message,
                suggestion = "This is a transient Azure error. Wait 30 seconds and retry the same tool call."
            });
        }
        catch (RequestFailedException ex) when (ex.Status == 403)
        {
            return JsonSerializer.Serialize(new
            {
                error = true,
                error_type = "authorization",
                message = ex.Message,
                suggestion = "The service principal lacks permission for this operation. Inform the user and do not retry."
            });
        }
        catch (RequestFailedException ex)
        {
            return JsonSerializer.Serialize(new
            {
                error = true,
                error_type = "azure_api",
                http_status = ex.Status,
                error_code = ex.ErrorCode,
                message = ex.Message
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = true, error_type = "internal", message = ex.Message });
        }
    }

    private async Task<string> GetInfrastructureGraphAsync(JsonElement input, string defaultSub, CancellationToken ct)
    {
        var sub = input.Str("subscription_id") ?? defaultSub;
        var graph = await _resourceService.GetInfrastructureGraphSummaryAsync(sub);
        return JsonSerializer.Serialize(graph);
    }

    private async Task<string> GetResourceAsync(JsonElement input, CancellationToken ct)
    {
        var result = await _genericResources.GetAsync(input.Str("resource_id")!, ct);
        return JsonSerializer.Serialize(result);
    }

    private async Task<string> GetDeploymentStatusAsync(JsonElement input, CancellationToken ct)
    {
        var result = await _deploymentService.GetDeploymentAsync(
            input.Str("subscription_id")!,
            input.Str("resource_group_name"),
            input.Str("deployment_name")!,
            ct);
        return JsonSerializer.Serialize(result);
    }

    private string CreatePlan(JsonElement input, string sessionId)
    {
        var planId = _planStore.CreatePlan(sessionId, input);
        return JsonSerializer.Serialize(new { plan_id = planId, status = "awaiting_user_approval" });
    }

    private async Task<string> DeployArmTemplateAsync(JsonElement input, CancellationToken ct)
    {
        if (!ValidatePlanApproved(input, out var planError))
            return planError!;

        var manifest = new DeploymentManifestRequest
        {
            SubscriptionId = input.Str("subscription_id")!,
            DeploymentName = input.Str("deployment_name")!,
            TemplateJson = input.Str("template_json")!,
            ParametersJson = input.Str("parameters_json"),
            ResourceGroupName = input.Str("resource_group_name"),
            Location = input.Str("location"),
            Mode = input.Str("mode") ?? "Incremental",
            WaitForCompletion = true
        };

        var approval = _approvalService.CreateApproval(manifest);
        if (!_approvalService.TryConsume(approval.ApprovalId.ToString(), manifest, out var err))
            return JsonSerializer.Serialize(new { error = true, error_type = "internal", message = err });

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            var result = await _deploymentService.CreateOrUpdateAsync(manifest.ToApplyInput(), ct);
            if (result.Succeeded || attempt == 3) return JsonSerializer.Serialize(result);
            if (result.HttpStatus is 429 or 503)
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt) * 5), ct);
            else
                return JsonSerializer.Serialize(result);
        }

        return JsonSerializer.Serialize(new { error = true, message = "Deployment failed after 3 attempts." });
    }

    private async Task<string> ApplyResourceMutationAsync(JsonElement input, CancellationToken ct)
    {
        if (!ValidatePlanApproved(input, out var planError))
            return planError!;

        var operation = Enum.Parse<ResourceMutationOperation>(input.Str("operation")!);
        var manifest = new ResourceMutationManifestRequest
        {
            ResourceId = input.Str("resource_id")!,
            Operation = operation,
            Location = input.Str("location"),
            PropertiesJson = input.Str("properties_json"),
            SkuJson = input.Str("sku_json"),
            Kind = input.Str("kind"),
            WaitForCompletion = true
        };

        if (input.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Object)
            manifest.Tags = tagsEl.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");

        var approval = _mutationApprovals.CreateApproval(manifest);
        if (!_mutationApprovals.TryConsume(approval.ApprovalId.ToString(), manifest, out var err))
            return JsonSerializer.Serialize(new { error = true, error_type = "internal", message = err });

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            var result = operation == ResourceMutationOperation.Delete
                ? await _genericResources.DeleteAsync(manifest.ResourceId, true, ct)
                : await _genericResources.CreateOrUpdateAsync(
                    manifest.ResourceId, manifest.Location!, manifest.PropertiesJson,
                    manifest.Tags, manifest.SkuJson, manifest.Kind, true, ct);

            if (result.Succeeded || attempt == 3) return JsonSerializer.Serialize(result);
            if (result.HttpStatus is 429 or 503)
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt) * 5), ct);
            else
                return JsonSerializer.Serialize(result);
        }

        return JsonSerializer.Serialize(new { error = true, message = "Mutation failed after 3 attempts." });
    }

    private bool ValidatePlanApproved(JsonElement input, out string? error)
    {
        error = null;
        var planIdStr = input.Str("plan_id");
        if (string.IsNullOrWhiteSpace(planIdStr) || !Guid.TryParse(planIdStr, out var planId))
        {
            error = JsonSerializer.Serialize(new
            {
                error = true,
                error_type = "missing_plan",
                message = "You must call create_plan first and receive user approval before calling write tools. Do not retry until a plan is approved."
            });
            return false;
        }

        var status = _planStore.GetStatus(planId);
        if (status != PlanStatus.Approved)
        {
            var reason = status switch
            {
                PlanStatus.Pending => "The plan is still awaiting user approval. Do not retry until the user approves.",
                PlanStatus.Rejected => "The plan was rejected by the user. Do not proceed.",
                null => "The plan_id is unknown or expired. Call create_plan again.",
                _ => "Plan is not approved."
            };
            error = JsonSerializer.Serialize(new { error = true, error_type = "plan_not_approved", message = reason });
            return false;
        }

        return true;
    }

    private static IReadOnlyList<ToolDefinition> BuildDefinitions() =>
    [
        Tool("get_infrastructure_graph",
            "Returns all Azure resources and dependency edges for a subscription. Call this to understand what exists before planning changes.",
            """
            {
              "type": "object",
              "properties": {
                "subscription_id": {"type": "string", "description": "Azure subscription ID"}
              },
              "required": ["subscription_id"]
            }
            """),
        Tool("get_resource",
            "Fetches a single Azure resource by its full ARM resource ID.",
            """
            {
              "type": "object",
              "properties": {
                "resource_id": {"type": "string", "description": "Full ARM resource ID"}
              },
              "required": ["resource_id"]
            }
            """),
        Tool("get_deployment_status",
            "Check provisioning status of an ARM deployment by name.",
            """
            {
              "type": "object",
              "properties": {
                "deployment_name": {"type": "string"},
                "subscription_id": {"type": "string"},
                "resource_group_name": {"type": "string", "description": "Omit for subscription-scoped deployments"}
              },
              "required": ["deployment_name", "subscription_id"]
            }
            """),
        Tool("create_plan",
            "REQUIRED: call this before any write operation (deploy_arm_template or apply_resource_mutation). Presents a structured plan to the user for approval. Returns a plan_id you must pass to write tools.",
            """
            {
              "type": "object",
              "properties": {
                "title": {"type": "string", "description": "Short title for this plan"},
                "operations": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "action": {"type": "string", "enum": ["Create", "Update", "Delete", "Deploy"]},
                      "resource_type": {"type": "string"},
                      "resource_name": {"type": "string"},
                      "resource_group": {"type": "string"},
                      "details": {"type": "string"}
                    },
                    "required": ["action", "resource_type", "resource_name"]
                  }
                },
                "risk_level": {"type": "string", "enum": ["Low", "Medium", "High"]},
                "estimated_cost_note": {"type": "string"}
              },
              "required": ["title", "operations"]
            }
            """),
        Tool("deploy_arm_template",
            "Deploys an ARM template to Azure. Requires an approved plan_id from create_plan.",
            """
            {
              "type": "object",
              "properties": {
                "plan_id": {"type": "string", "description": "Approved plan_id from create_plan"},
                "subscription_id": {"type": "string"},
                "deployment_name": {"type": "string"},
                "template_json": {"type": "string", "description": "Full ARM template as a JSON string"},
                "parameters_json": {"type": "string", "description": "ARM parameters JSON string (optional)"},
                "resource_group_name": {"type": "string", "description": "Omit for subscription-scoped deployments"},
                "location": {"type": "string", "description": "Required for subscription-scoped deployments"},
                "mode": {"type": "string", "enum": ["Incremental", "Complete"]}
              },
              "required": ["plan_id", "subscription_id", "deployment_name", "template_json"]
            }
            """),
        Tool("apply_resource_mutation",
            "Creates, updates, or deletes a single Azure resource. Requires an approved plan_id from create_plan.",
            """
            {
              "type": "object",
              "properties": {
                "plan_id": {"type": "string", "description": "Approved plan_id from create_plan"},
                "resource_id": {"type": "string", "description": "Full ARM resource ID"},
                "operation": {"type": "string", "enum": ["CreateOrUpdate", "Delete"]},
                "location": {"type": "string", "description": "Required for CreateOrUpdate"},
                "properties_json": {"type": "string", "description": "Resource properties as JSON object string"},
                "tags": {"type": "object", "additionalProperties": {"type": "string"}},
                "sku_json": {"type": "string", "description": "SKU JSON, e.g. {\"name\":\"Standard_LRS\"}"},
                "kind": {"type": "string"}
              },
              "required": ["plan_id", "resource_id", "operation"]
            }
            """)
    ];

    private static ToolDefinition Tool(string name, string description, string schemaJson) => new()
    {
        Name = name,
        Description = description,
        InputSchema = JsonNode.Parse(schemaJson)!
    };
}

internal static class JsonElementExtensions
{
    public static string? Str(this JsonElement el, string property) =>
        el.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
}
