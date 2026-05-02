using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using InfraMapper.Models;
using InfraMapper.Services;

namespace InfraMapper.Services.Agent.Tools;

/// <summary>
/// Tool implementations for the OrchestratorAgent.
/// Each public method is wrapped as an AIFunction via AIFunctionFactory.Create.
/// One instance is created per agent session so that sessionId is always correct.
/// </summary>
public sealed class OrchestratorTools
{
    private readonly AzureResourceService _resourceService;
    private readonly IArmDeploymentService _deploymentService;
    private readonly IApprovalService _approvalService;
    private readonly IResourceMutationApprovalService _mutationApprovals;
    private readonly IArmGenericResourceService _genericResources;
    private readonly PlanStore _planStore;
    private readonly string _sessionId;
    private readonly ILogger<OrchestratorTools> _logger;

    internal static readonly JsonSerializerOptions SnakeCaseOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OrchestratorTools(
        AzureResourceService resourceService,
        IArmDeploymentService deploymentService,
        IApprovalService approvalService,
        IResourceMutationApprovalService mutationApprovals,
        IArmGenericResourceService genericResources,
        PlanStore planStore,
        string sessionId,
        ILogger<OrchestratorTools> logger)
    {
        _resourceService = resourceService;
        _deploymentService = deploymentService;
        _approvalService = approvalService;
        _mutationApprovals = mutationApprovals;
        _genericResources = genericResources;
        _planStore = planStore;
        _sessionId = sessionId;
        _logger = logger;
    }

    [Description("Returns all Azure resources and dependency edges for a subscription. Call this to understand what exists before planning changes.")]
    public async Task<string> GetInfrastructureGraphAsync(
        [Description("Azure subscription ID")] string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Tool: get_infrastructure_graph | sub={Sub}", subscriptionId);
        try
        {
            var graph = await _resourceService.GetInfrastructureGraphSummaryAsync(subscriptionId);
            return JsonSerializer.Serialize(graph);
        }
        catch (RequestFailedException ex) when (ex.Status is 429 or 503) { return TransientError(ex); }
        catch (RequestFailedException ex) when (ex.Status == 403) { return AuthError(ex); }
        catch (RequestFailedException ex) { return AzureError(ex); }
        catch (Exception ex) { return InternalError(ex); }
    }

    [Description("Fetches a single Azure resource by its full ARM resource ID.")]
    public async Task<string> GetResourceAsync(
        [Description("Full ARM resource ID")] string resourceId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Tool: get_resource | id={Id}", resourceId);
        try
        {
            var result = await _genericResources.GetAsync(resourceId, cancellationToken);
            return JsonSerializer.Serialize(result);
        }
        catch (RequestFailedException ex) when (ex.Status is 429 or 503) { return TransientError(ex); }
        catch (RequestFailedException ex) when (ex.Status == 403) { return AuthError(ex); }
        catch (RequestFailedException ex) { return AzureError(ex); }
        catch (Exception ex) { return InternalError(ex); }
    }

    [Description("Check provisioning status of an ARM deployment by name.")]
    public async Task<string> GetDeploymentStatusAsync(
        [Description("Azure subscription ID")] string subscriptionId,
        [Description("Deployment name")] string deploymentName,
        [Description("Resource group name; omit for subscription-scoped deployments")] string? resourceGroupName = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Tool: get_deployment_status | deployment={Name}", deploymentName);
        try
        {
            var result = await _deploymentService.GetDeploymentAsync(subscriptionId, resourceGroupName, deploymentName, cancellationToken);
            return JsonSerializer.Serialize(result);
        }
        catch (RequestFailedException ex) when (ex.Status is 429 or 503) { return TransientError(ex); }
        catch (RequestFailedException ex) when (ex.Status == 403) { return AuthError(ex); }
        catch (RequestFailedException ex) { return AzureError(ex); }
        catch (Exception ex) { return InternalError(ex); }
    }

    [Description("Deploys an ARM template to Azure. Requires an approved plan_id from plan_deployment.")]
    public async Task<string> DeployArmTemplateAsync(
        [Description("Approved plan_id from create_plan")] string planId,
        [Description("Azure subscription ID")] string subscriptionId,
        [Description("Deployment name")] string deploymentName,
        [Description("Full ARM template as a JSON string")] string templateJson,
        [Description("ARM parameters JSON string (optional)")] string? parametersJson = null,
        [Description("Resource group name; omit for subscription-scoped deployments")] string? resourceGroupName = null,
        [Description("Required for subscription-scoped deployments")] string? location = null,
        [Description("Deployment mode: Incremental or Complete")] string mode = "Incremental",
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Tool: deploy_arm_template | deployment={Name}", deploymentName);
        if (!ValidatePlanApproved(planId, out var planError))
            return planError!;

        var manifest = new DeploymentManifestRequest
        {
            SubscriptionId = subscriptionId,
            DeploymentName = deploymentName,
            TemplateJson = templateJson,
            ParametersJson = parametersJson,
            ResourceGroupName = resourceGroupName,
            Location = location,
            Mode = mode,
            WaitForCompletion = true
        };

        var approval = _approvalService.CreateApproval(manifest);
        if (!_approvalService.TryConsume(approval.ApprovalId.ToString(), manifest, out var err))
            return JsonSerializer.Serialize(new { error = true, error_type = "internal", message = err });

        try
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                var result = await _deploymentService.CreateOrUpdateAsync(manifest.ToApplyInput(), cancellationToken);
                if (result.Succeeded || attempt == 3) return JsonSerializer.Serialize(result);
                if (result.HttpStatus is 429 or 503)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt) * 5), cancellationToken);
                else
                    return JsonSerializer.Serialize(result);
            }
        }
        catch (RequestFailedException ex) when (ex.Status is 429 or 503) { return TransientError(ex); }
        catch (RequestFailedException ex) when (ex.Status == 403) { return AuthError(ex); }
        catch (RequestFailedException ex) { return AzureError(ex); }
        catch (Exception ex) { return InternalError(ex); }

        return JsonSerializer.Serialize(new { error = true, message = "Deployment failed after 3 attempts." });
    }

    [Description("Creates, updates, or deletes a single Azure resource. Requires an approved plan_id from create_plan.")]
    public async Task<string> ApplyResourceMutationAsync(
        [Description("Approved plan_id from create_plan")] string planId,
        [Description("Full ARM resource ID")] string resourceId,
        [Description("Operation: CreateOrUpdate or Delete")] string operation,
        [Description("Resource location; required for CreateOrUpdate")] string? location = null,
        [Description("Resource properties as JSON object string")] string? propertiesJson = null,
        [Description("Tags to apply to the resource")] Dictionary<string, string>? tags = null,
        [Description("SKU JSON, e.g. {\"name\":\"Standard_LRS\"}")] string? skuJson = null,
        [Description("Resource kind")] string? kind = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Tool: apply_resource_mutation | resource={Id}", resourceId);
        if (!ValidatePlanApproved(planId, out var planError))
            return planError!;

        if (!Enum.TryParse<ResourceMutationOperation>(operation, out var opEnum))
            return JsonSerializer.Serialize(new { error = true, message = $"Invalid operation '{operation}'. Use CreateOrUpdate or Delete." });

        var manifest = new ResourceMutationManifestRequest
        {
            ResourceId = resourceId,
            Operation = opEnum,
            Location = location,
            PropertiesJson = propertiesJson,
            Tags = tags,
            SkuJson = skuJson,
            Kind = kind,
            WaitForCompletion = true
        };

        var approval = _mutationApprovals.CreateApproval(manifest);
        if (!_mutationApprovals.TryConsume(approval.ApprovalId.ToString(), manifest, out var err))
            return JsonSerializer.Serialize(new { error = true, error_type = "internal", message = err });

        try
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                var result = opEnum == ResourceMutationOperation.Delete
                    ? await _genericResources.DeleteAsync(manifest.ResourceId, true, cancellationToken)
                    : await _genericResources.CreateOrUpdateAsync(
                        manifest.ResourceId, manifest.Location!, manifest.PropertiesJson,
                        manifest.Tags, manifest.SkuJson, manifest.Kind, true, cancellationToken);

                if (result.Succeeded || attempt == 3) return JsonSerializer.Serialize(result);
                if (result.HttpStatus is 429 or 503)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt) * 5), cancellationToken);
                else
                    return JsonSerializer.Serialize(result);
            }
        }
        catch (RequestFailedException ex) when (ex.Status is 429 or 503) { return TransientError(ex); }
        catch (RequestFailedException ex) when (ex.Status == 403) { return AuthError(ex); }
        catch (RequestFailedException ex) { return AzureError(ex); }
        catch (Exception ex) { return InternalError(ex); }

        return JsonSerializer.Serialize(new { error = true, message = "Mutation failed after 3 attempts." });
    }

    private bool ValidatePlanApproved(string planIdStr, out string? error)
    {
        error = null;
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

    private static string TransientError(RequestFailedException ex) => JsonSerializer.Serialize(new
    {
        error = true, error_type = "transient", message = ex.Message,
        suggestion = "This is a transient Azure error. Wait 30 seconds and retry the same tool call."
    });

    private static string AuthError(RequestFailedException ex) => JsonSerializer.Serialize(new
    {
        error = true, error_type = "authorization", message = ex.Message,
        suggestion = "The service principal lacks permission for this operation. Inform the user and do not retry."
    });

    private static string AzureError(RequestFailedException ex) => JsonSerializer.Serialize(new
    {
        error = true, error_type = "azure_api",
        http_status = ex.Status, error_code = ex.ErrorCode, message = ex.Message
    });

    private static string InternalError(Exception ex) => JsonSerializer.Serialize(new
    {
        error = true, error_type = "internal", message = ex.Message
    });
}

/// <summary>DTO for a single operation within a create_plan call.</summary>
public record PlanOperationDto(
    [property: Description("Action to take: Create, Update, Delete, or Deploy")] string Action,
    [property: Description("Azure resource type, e.g. Microsoft.Storage/storageAccounts")] string ResourceType,
    [property: Description("Resource name")] string ResourceName,
    [property: Description("Resource group")] string? ResourceGroup = null,
    [property: Description("Additional details about the operation")] string? Details = null);
