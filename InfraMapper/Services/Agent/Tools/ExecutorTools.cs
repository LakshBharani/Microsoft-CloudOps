using System.ComponentModel;
using System.Text.Json;
using Azure;
using InfraMapper.Models;
using InfraMapper.Services;
using Microsoft.SemanticKernel;

namespace InfraMapper.Services.Agent.Tools;

public sealed class ExecutorTools
{
    private static readonly AsyncLocal<string?> CurrentPlanId = new();

    private readonly IArmDeploymentService _deploymentService;
    private readonly IApprovalService _approvalService;
    private readonly IResourceMutationApprovalService _mutationApprovals;
    private readonly IArmGenericResourceService _genericResources;
    private readonly PlanStore _planStore;
    private readonly string _sessionId;
    private readonly string _subscriptionId;

    public ExecutorTools(
        IArmDeploymentService deploymentService,
        IApprovalService approvalService,
        IResourceMutationApprovalService mutationApprovals,
        IArmGenericResourceService genericResources,
        PlanStore planStore,
        string sessionId,
        string subscriptionId)
    {
        _deploymentService = deploymentService;
        _approvalService = approvalService;
        _mutationApprovals = mutationApprovals;
        _genericResources = genericResources;
        _planStore = planStore;
        _sessionId = sessionId;
        _subscriptionId = subscriptionId;
    }

    public static IDisposable UsePlan(string planId)
    {
        var prior = CurrentPlanId.Value;
        CurrentPlanId.Value = planId;
        return new PlanScope(prior);
    }

    [KernelFunction("deploy_arm_template")]
    [Description("Deploy an ARM template to Azure. Requires an approved plan_id. " +
                 "Returns success:true on completion, or needs_replan:true if the template is invalid.")]
    public async Task<string> DeployArmTemplateAsync(
        [Description("Approved plan_id from plan_deployment. If omitted, backend uses the current approved execution plan.")] string? plan_id = null,
        [Description("Azure subscription ID. Ignored by backend; session subscription is used.")] string subscription_id = "",
        [Description("Deployment name")] string deployment_name = "",
        [Description("Full ARM template as a JSON string")] string template_json = "",
        [Description("ARM parameters JSON string (optional)")] string? parameters_json = null,
        [Description("Resource group name; omit for subscription-scoped deployments")] string? resource_group_name = null,
        [Description("Required for subscription-scoped deployments")] string? location = null,
        [Description("Deployment mode: Incremental or Complete")] string mode = "Incremental",
        CancellationToken cancellationToken = default)
    {
        plan_id = ResolvePlanId(plan_id);
        if (!ValidatePlanApproved(plan_id, out var planError))
            return planError!;

        var manifest = new DeploymentManifestRequest
        {
            SubscriptionId = _subscriptionId,
            DeploymentName = deployment_name,
            TemplateJson = template_json,
            ParametersJson = parameters_json,
            ResourceGroupName = resource_group_name,
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
                        deployment_name,
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

    [KernelFunction("apply_resource_mutation")]
    [Description("Create, update, or delete a single Azure resource. Requires an approved plan_id.")]
    public async Task<string> ApplyResourceMutationAsync(
        [Description("Approved plan_id from plan_deployment. If omitted, backend uses the current approved execution plan.")] string? plan_id = null,
        [Description("Full ARM resource ID")] string resource_id = "",
        [Description("Operation: CreateOrUpdate or Delete")] string operation = "",
        [Description("Resource location; required for CreateOrUpdate")] string? location = null,
        [Description("Resource properties as JSON object string")] string? properties_json = null,
        [Description("Tags to apply to the resource")] Dictionary<string, string>? tags = null,
        [Description("SKU JSON, e.g. {\"name\":\"Standard_LRS\"}")] string? sku_json = null,
        [Description("Resource kind")] string? kind = null,
        CancellationToken cancellationToken = default)
    {
        plan_id = ResolvePlanId(plan_id);
        if (!ValidatePlanApproved(plan_id, out var planError))
            return planError!;

        resource_id = NormalizeSubscriptionInResourceId(resource_id);

        if (!Enum.TryParse<ResourceMutationOperation>(operation, out var opEnum))
            return JsonSerializer.Serialize(new { error = true, message = $"Invalid operation '{operation}'." });

        var manifest = new ResourceMutationManifestRequest
        {
            ResourceId = resource_id,
            Operation = opEnum,
            Location = location,
            PropertiesJson = properties_json,
            Tags = tags,
            SkuJson = sku_json,
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
                        resource_id,
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

    [KernelFunction("get_plan_details")]
    [Description("Retrieve the full details of a plan by its plan_id, including all operations and metadata.")]
    public string GetPlanDetails(
        [Description("The plan_id to retrieve. If omitted, backend uses the current approved execution plan.")] string? plan_id = null)
    {
        plan_id = ResolvePlanId(plan_id);
        if (!Guid.TryParse(plan_id, out var guid))
            return JsonSerializer.Serialize(new { error = true, message = "Invalid plan_id format." });

        var data = _planStore.GetPlanData(guid);
        if (data is null)
            return JsonSerializer.Serialize(new { error = true, message = "Plan not found or expired." });

        return data.Value.GetRawText();
    }

    [KernelFunction("get_deployment_status")]
    [Description("Check provisioning status of an ARM deployment by name.")]
    public async Task<string> GetDeploymentStatusAsync(
        [Description("Azure subscription ID. Ignored by backend; session subscription is used.")] string subscription_id,
        [Description("Deployment name")] string deployment_name,
        [Description("Resource group name; omit for subscription-scoped deployments")] string? resource_group_name = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _deploymentService.GetDeploymentAsync(
                _subscriptionId, resource_group_name, deployment_name, cancellationToken);
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

    private static string? ResolvePlanId(string? planId) =>
        string.IsNullOrWhiteSpace(planId) ? CurrentPlanId.Value : planId;

    private bool ValidatePlanApproved(string? planIdStr, out string? error)
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

    private sealed class PlanScope : IDisposable
    {
        private readonly string? _prior;

        public PlanScope(string? prior)
        {
            _prior = prior;
        }

        public void Dispose()
        {
            CurrentPlanId.Value = _prior;
        }
    }

    private static string ClassifyError(int? httpStatus) => httpStatus switch
    {
        400 => "validation",
        404 => "not_found",
        409 => "conflict",
        422 => "validation",
        _ => "azure_api"
    };

    private string NormalizeSubscriptionInResourceId(string resourceId)
    {
        const string marker = "/subscriptions/";
        var start = resourceId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return resourceId;

        var idStart = start + marker.Length;
        var idEnd = resourceId.IndexOf('/', idStart);
        if (idEnd < 0) return marker + _subscriptionId;

        return resourceId[..idStart] + _subscriptionId + resourceId[idEnd..];
    }
}
