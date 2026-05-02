using System.ComponentModel;
using System.Text.Json;
using Azure;
using InfraMapper.Models;
using InfraMapper.Services;

namespace InfraMapper.Services.Agent.Tools;

/// <summary>
/// Tools available to the ExecutorAgent.
/// Wraps the ARM deployment services with error classification:
///   - Transient errors (429/503) are retried internally (up to 3 attempts).
///   - Validation/quota errors return needs_replan:true so the Orchestrator can re-plan.
/// </summary>
public sealed class ExecutorTools
{
    private readonly IArmDeploymentService _deploymentService;
    private readonly IApprovalService _approvalService;
    private readonly IResourceMutationApprovalService _mutationApprovals;
    private readonly IArmGenericResourceService _genericResources;
    private readonly PlanStore _planStore;
    private readonly string _sessionId;

    public ExecutorTools(
        IArmDeploymentService deploymentService,
        IApprovalService approvalService,
        IResourceMutationApprovalService mutationApprovals,
        IArmGenericResourceService genericResources,
        PlanStore planStore,
        string sessionId)
    {
        _deploymentService = deploymentService;
        _approvalService = approvalService;
        _mutationApprovals = mutationApprovals;
        _genericResources = genericResources;
        _planStore = planStore;
        _sessionId = sessionId;
    }

    [Description("Deploy an ARM template to Azure. Requires an approved plan_id. " +
                 "Returns success:true on completion, or needs_replan:true if the template is invalid.")]
    public async Task<string> DeployArmTemplateAsync(
        [Description("Approved plan_id from plan_deployment")] string planId,
        [Description("Azure subscription ID")] string subscriptionId,
        [Description("Deployment name")] string deploymentName,
        [Description("Full ARM template as a JSON string")] string templateJson,
        [Description("ARM parameters JSON string (optional)")] string? parametersJson = null,
        [Description("Resource group name; omit for subscription-scoped deployments")] string? resourceGroupName = null,
        [Description("Required for subscription-scoped deployments")] string? location = null,
        [Description("Deployment mode: Incremental or Complete")] string mode = "Incremental",
        CancellationToken cancellationToken = default)
    {
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
                if (result.Succeeded) return JsonSerializer.Serialize(result);
                if (attempt == 3) break;
                if (result.HttpStatus is 429 or 503)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt) * 5), cancellationToken);
                else
                    // Non-transient failure (e.g. invalid template, quota exceeded) — signal replan.
                    return JsonSerializer.Serialize(new
                    {
                        needs_replan = true,
                        error_type = ClassifyError(result.HttpStatus),
                        http_status = result.HttpStatus,
                        message = result.ErrorMessage,
                        deployment_name = deploymentName,
                    });
            }
        }
        catch (RequestFailedException ex) when (ex.Status is 429 or 503)
        {
            return JsonSerializer.Serialize(new { error = true, error_type = "transient", message = ex.Message });
        }
        catch (RequestFailedException ex)
        {
            return JsonSerializer.Serialize(new
            {
                needs_replan = true,
                error_type = "azure_api",
                http_status = ex.Status,
                error_code = ex.ErrorCode,
                message = ex.Message,
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = true, error_type = "internal", message = ex.Message });
        }

        return JsonSerializer.Serialize(new { error = true, message = "Deployment failed after 3 attempts." });
    }

    [Description("Create, update, or delete a single Azure resource. Requires an approved plan_id.")]
    public async Task<string> ApplyResourceMutationAsync(
        [Description("Approved plan_id from plan_deployment")] string planId,
        [Description("Full ARM resource ID")] string resourceId,
        [Description("Operation: CreateOrUpdate or Delete")] string operation,
        [Description("Resource location; required for CreateOrUpdate")] string? location = null,
        [Description("Resource properties as JSON object string")] string? propertiesJson = null,
        [Description("Tags to apply to the resource")] Dictionary<string, string>? tags = null,
        [Description("SKU JSON, e.g. {\"name\":\"Standard_LRS\"}")] string? skuJson = null,
        [Description("Resource kind")] string? kind = null,
        CancellationToken cancellationToken = default)
    {
        if (!ValidatePlanApproved(planId, out var planError))
            return planError!;

        if (!Enum.TryParse<ResourceMutationOperation>(operation, out var opEnum))
            return JsonSerializer.Serialize(new { error = true, message = $"Invalid operation '{operation}'." });

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

                if (result.Succeeded) return JsonSerializer.Serialize(result);
                if (attempt == 3) break;
                if (result.HttpStatus is 429 or 503)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt) * 5), cancellationToken);
                else
                    return JsonSerializer.Serialize(new
                    {
                        needs_replan = true,
                        error_type = ClassifyError(result.HttpStatus),
                        http_status = result.HttpStatus,
                        message = result.ErrorMessage,
                        resource_id = resourceId,
                    });
            }
        }
        catch (RequestFailedException ex) when (ex.Status is 429 or 503)
        {
            return JsonSerializer.Serialize(new { error = true, error_type = "transient", message = ex.Message });
        }
        catch (RequestFailedException ex)
        {
            return JsonSerializer.Serialize(new
            {
                needs_replan = true,
                error_type = "azure_api",
                http_status = ex.Status,
                error_code = ex.ErrorCode,
                message = ex.Message,
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = true, error_type = "internal", message = ex.Message });
        }

        return JsonSerializer.Serialize(new { error = true, message = "Mutation failed after 3 attempts." });
    }

    [Description("Check provisioning status of an ARM deployment by name.")]
    public async Task<string> GetDeploymentStatusAsync(
        [Description("Azure subscription ID")] string subscriptionId,
        [Description("Deployment name")] string deploymentName,
        [Description("Resource group name; omit for subscription-scoped deployments")] string? resourceGroupName = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _deploymentService.GetDeploymentAsync(
                subscriptionId, resourceGroupName, deploymentName, cancellationToken);
            return JsonSerializer.Serialize(result);
        }
        catch (RequestFailedException ex) when (ex.Status is 429 or 503)
        {
            return JsonSerializer.Serialize(new { error = true, error_type = "transient", message = ex.Message });
        }
        catch (RequestFailedException ex)
        {
            return JsonSerializer.Serialize(new { error = true, error_type = "azure_api", message = ex.Message });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = true, error_type = "internal", message = ex.Message });
        }
    }

    private bool ValidatePlanApproved(string planIdStr, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(planIdStr) || !Guid.TryParse(planIdStr, out var planId))
        {
            error = JsonSerializer.Serialize(new
            {
                error = true, error_type = "missing_plan",
                message = "You must use an approved plan_id from plan_deployment. Do not retry until approved."
            });
            return false;
        }

        var status = _planStore.GetStatus(planId);
        if (status != PlanStatus.Approved)
        {
            var reason = status switch
            {
                PlanStatus.Pending  => "Plan is still awaiting user approval. Do not proceed until approved.",
                PlanStatus.Rejected => "Plan was rejected by the user. Do not proceed.",
                null                => "Plan not found or expired. Call plan_deployment again.",
                _                   => "Plan is not approved."
            };
            error = JsonSerializer.Serialize(new { error = true, error_type = "plan_not_approved", message = reason });
            return false;
        }

        return true;
    }

    private static string ClassifyError(int? httpStatus) => httpStatus switch
    {
        400 => "validation",
        404 => "not_found",
        409 => "conflict",
        422 => "validation",
        _ => "azure_api"
    };
}
