using System.Net;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using InfraMapper.Models;

namespace InfraMapper.Services;

public sealed class ArmDeploymentService : IArmDeploymentService
{
    private readonly ArmClient _armClient;

    public ArmDeploymentService(ArmClient armClient)
    {
        _armClient = armClient;
    }

    public async Task<ArmDeploymentApplyResult> ValidateAsync(
        ArmDeploymentApplyInput input,
        CancellationToken cancellationToken = default)
    {
        var invalid = ValidateInput(input);
        if (invalid is not null)
            return invalid;

        try
        {
            var scope = BuildScope(input.SubscriptionId, input.ResourceGroupName);
            var content = BuildContent(input, IsSubscriptionScope(input));
            var deployment = _armClient.GetArmDeploymentResource(ResourceIdentifier.Parse($"{scope}/providers/Microsoft.Resources/deployments/{input.DeploymentName}"));
            var operation = await deployment
                .ValidateAsync(WaitUntil.Completed, content, cancellationToken)
                .ConfigureAwait(false);

            var result = operation.Value;
            if (result.Error is not null)
                return Fail(
                    scope,
                    input.DeploymentName,
                    (int)HttpStatusCode.BadRequest,
                    result.Error.Message ?? result.Error.Code ?? "ARM template validation failed.",
                    result.Error.Code);

            return new ArmDeploymentApplyResult
            {
                Succeeded = true,
                DeploymentName = input.DeploymentName,
                Scope = scope,
                ProvisioningState = result.Properties?.ProvisioningState?.ToString()
            };
        }
        catch (RequestFailedException ex)
        {
            return Fail(BuildScope(input.SubscriptionId, input.ResourceGroupName), input.DeploymentName, ex.Status, ex.Message, ex.ErrorCode);
        }
    }

    public async Task<ArmDeploymentApplyResult> CreateOrUpdateAsync(
        ArmDeploymentApplyInput input,
        CancellationToken cancellationToken = default)
    {
        var invalid = ValidateInput(input);
        if (invalid is not null)
            return invalid;

        var content = BuildContent(input, IsSubscriptionScope(input));

        var wait = input.WaitForCompletion ? WaitUntil.Completed : WaitUntil.Started;

        var subscriptionId = SubscriptionResource.CreateResourceIdentifier(input.SubscriptionId);
        var subscriptionOp = await _armClient.GetSubscriptionResource(subscriptionId).GetAsync(cancellationToken).ConfigureAwait(false);
        var subscription = subscriptionOp.Value;

        string scope;
        ArmDeploymentCollection deployments;

        if (string.IsNullOrWhiteSpace(input.ResourceGroupName))
        {
            scope = $"/subscriptions/{input.SubscriptionId}";
            deployments = subscription.GetArmDeployments();
        }
        else
        {
            scope = $"/subscriptions/{input.SubscriptionId}/resourceGroups/{input.ResourceGroupName}";
            var rgResponse = await subscription
                .GetResourceGroupAsync(input.ResourceGroupName, cancellationToken)
                .ConfigureAwait(false);
            deployments = rgResponse.Value.GetArmDeployments();
        }

        try
        {
            var operation = await deployments
                .CreateOrUpdateAsync(wait, input.DeploymentName, content, cancellationToken)
                .ConfigureAwait(false);

            var deployment = operation.Value;
            return MapSuccess(scope, deployment);
        }
        catch (RequestFailedException ex)
        {
            return Fail(scope, input.DeploymentName, ex.Status, ex.Message, ex.ErrorCode);
        }
    }

    public async Task<ArmDeploymentApplyResult> GetDeploymentAsync(
        string subscriptionId,
        string? resourceGroupName,
        string deploymentName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
            return Fail(null, null, null, "SubscriptionId is required.");

        if (string.IsNullOrWhiteSpace(deploymentName))
            return Fail(null, null, null, "DeploymentName is required.");

        string scope;
        try
        {
            var subscriptionOp = await _armClient
                .GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(subscriptionId))
                .GetAsync(cancellationToken)
                .ConfigureAwait(false);
            var subscription = subscriptionOp.Value;

            Response<ArmDeploymentResource> deploymentResponse;

            if (string.IsNullOrWhiteSpace(resourceGroupName))
            {
                scope = $"/subscriptions/{subscriptionId}";
                deploymentResponse = await subscription
                    .GetArmDeployments()
                    .GetAsync(deploymentName, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                scope = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}";
                var rgResponse = await subscription
                    .GetResourceGroupAsync(resourceGroupName, cancellationToken)
                    .ConfigureAwait(false);
                deploymentResponse = await rgResponse.Value
                    .GetArmDeployments()
                    .GetAsync(deploymentName, cancellationToken)
                    .ConfigureAwait(false);
            }

            return MapSuccess(scope, deploymentResponse.Value);
        }
        catch (RequestFailedException ex)
        {
            return Fail(
                string.IsNullOrWhiteSpace(resourceGroupName)
                    ? $"/subscriptions/{subscriptionId}"
                    : $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}",
                deploymentName,
                ex.Status,
                ex.Message,
                ex.ErrorCode);
        }
    }

    private static ArmDeploymentMode ParseMode(string? mode)
    {
        if (string.Equals(mode, "Complete", StringComparison.OrdinalIgnoreCase))
            return ArmDeploymentMode.Complete;
        return ArmDeploymentMode.Incremental;
    }

    private static ArmDeploymentApplyResult? ValidateInput(ArmDeploymentApplyInput input)
    {
        if (string.IsNullOrWhiteSpace(input.SubscriptionId))
            return Fail(null, null, null, "SubscriptionId is required.");

        if (string.IsNullOrWhiteSpace(input.DeploymentName))
            return Fail(null, null, null, "DeploymentName is required.");

        if (string.IsNullOrWhiteSpace(input.TemplateJson))
            return Fail(null, null, null, "TemplateJson is required.");

        try
        {
            using var _ = JsonDocument.Parse(input.TemplateJson);
        }
        catch (JsonException ex)
        {
            return Fail(null, null, null, $"TemplateJson is not valid JSON: {ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(input.ParametersJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(input.ParametersJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return Fail(null, null, null, "ParametersJson must be a JSON object.");
            }
            catch (JsonException ex)
            {
                return Fail(null, null, null, $"ParametersJson is not valid JSON: {ex.Message}");
            }
        }

        return null;
    }

    private static ArmDeploymentContent BuildContent(ArmDeploymentApplyInput input, bool isSubscriptionScope)
    {
        var props = new ArmDeploymentProperties(ParseMode(input.Mode))
        {
            Template = BinaryData.FromString(input.TemplateJson)
        };

        if (!string.IsNullOrWhiteSpace(input.ParametersJson))
            props.Parameters = BinaryData.FromString(input.ParametersJson);

        var content = new ArmDeploymentContent(props);
        if (isSubscriptionScope && !string.IsNullOrWhiteSpace(input.Location))
            content.Location = new AzureLocation(input.Location);

        return content;
    }

    private static bool IsSubscriptionScope(ArmDeploymentApplyInput input) =>
        string.IsNullOrWhiteSpace(input.ResourceGroupName);

    private static string BuildScope(string subscriptionId, string? resourceGroupName) =>
        string.IsNullOrWhiteSpace(resourceGroupName)
            ? $"/subscriptions/{subscriptionId}"
            : $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}";

    private static ArmDeploymentApplyResult MapSuccess(string scope, ArmDeploymentResource deployment)
    {
        var data = deployment.Data;
        var extended = data.Properties;
        var state = extended.ProvisioningState?.ToString();

        var succeeded = extended.ProvisioningState == ResourcesProvisioningState.Succeeded;

        string? errorCode = null;
        string? errorMessage = null;
        string? errorJson = null;

        if (extended.Error != null)
        {
            errorCode = extended.Error.Code;
            errorMessage = extended.Error.Message;
            try
            {
                errorJson = JsonSerializer.Serialize(extended.Error);
            }
            catch
            {
                errorJson = null;
            }

            if (extended.ProvisioningState == ResourcesProvisioningState.Failed)
                succeeded = false;
        }

        return new ArmDeploymentApplyResult
        {
            Succeeded = succeeded,
            DeploymentName = data.Name,
            Scope = scope,
            ProvisioningState = state,
            CorrelationId = extended.CorrelationId,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            ErrorDetailJson = errorJson
        };
    }

    private static ArmDeploymentApplyResult Fail(
        string? scope,
        string? deploymentName,
        int? httpStatus,
        string message,
        string? errorCode = null)
    {
        return new ArmDeploymentApplyResult
        {
            Succeeded = false,
            Scope = scope,
            DeploymentName = deploymentName,
            HttpStatus = httpStatus ?? (int)HttpStatusCode.BadRequest,
            ErrorCode = errorCode,
            ErrorMessage = message
        };
    }
}
