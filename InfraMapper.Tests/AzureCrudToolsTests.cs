using System.Text.Json;
using InfraMapper.Models;
using InfraMapper.Services;
using InfraMapper.Services.Agent;
using InfraMapper.Services.Agent.Runtime;
using InfraMapper.Services.Agent.Tools;
using ModelContextProtocol.Server;

namespace InfraMapper.Tests;

public sealed class AzureCrudToolsTests
{
    [Fact]
    public void CloudOpsMcpToolsExposeExpectedToolNames()
    {
        var names = typeof(CloudOpsMcpTools)
            .GetMethods()
            .Select(m => m.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: false)
                .OfType<McpServerToolAttribute>()
                .FirstOrDefault()?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Order()
            .ToArray();

        Assert.Equal(
        [
            "ask_clarifying_question",
            "create_or_update_resource",
            "create_plan",
            "delete_resource",
            "deploy_arm_template",
            "find_resource",
            "get_deployment_status",
            "get_resource",
            "list_resource_groups",
            "list_resources"
        ], names);
    }

    [Fact]
    public async Task DeployArmTemplateAcceptsArbitraryTemplateAndParametersObjects()
    {
        var deployments = new CapturingDeploymentService();
        var tools = new AzureCrudTools(
            resourceService: null!,
            genericResources: new CapturingGenericResourceService(),
            deployments: deployments,
            planStore: new PlanStore(),
            questionStore: new QuestionStore(),
            sessionId: "session-1",
            defaultSubscriptionId: "default-sub");

        var template = JsonSerializer.Deserialize<Dictionary<string, object?>>("""
            {
              "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
              "contentVersion": "1.0.0.0",
              "resources": [
                {
                  "type": "Microsoft.Network/networkSecurityGroups",
                  "apiVersion": "2023-11-01",
                  "name": "nsg1",
                  "location": "eastus",
                  "properties": { "securityRules": [] }
                }
              ]
            }
            """);
        var parameters = JsonSerializer.Deserialize<Dictionary<string, object?>>("""{ "env": { "value": "test" } }""");

        var json = await tools.DeployArmTemplateAsync(
            subscriptionId: "sub-1",
            resourceGroupName: "rg1",
            location: "eastus",
            deploymentName: "dep1",
            template: template,
            parameters: parameters);

        Assert.Contains("\"ok\":true", json);
        Assert.Equal(1, deployments.ValidateCalls);
        Assert.Equal(1, deployments.CreateCalls);
        Assert.Contains("Microsoft.Network/networkSecurityGroups", deployments.LastInput!.TemplateJson);
        Assert.Contains("\"env\"", deployments.LastInput.ParametersJson);
        Assert.Equal("rg1", deployments.LastInput.ResourceGroupName);
    }

    [Fact]
    public async Task DeployArmTemplateAcceptsTemplateJsonAlias()
    {
        var deployments = new CapturingDeploymentService();
        var tools = new AzureCrudTools(null!, new CapturingGenericResourceService(), deployments, new PlanStore(), new QuestionStore(), "s", "sub");
        var template = JsonSerializer.Deserialize<Dictionary<string, object?>>("""{ "contentVersion": "1.0.0.0", "resources": [] }""");
        var parameters = JsonSerializer.Deserialize<Dictionary<string, object?>>("""{ "env": { "value": "test" } }""");

        var json = await tools.DeployArmTemplateAsync(
            deploymentName: "dep-alias",
            template_json: template,
            parameters_json: parameters);

        Assert.Contains("\"ok\":true", json);
        Assert.Equal(1, deployments.ValidateCalls);
        Assert.Equal(1, deployments.CreateCalls);
        Assert.Contains("contentVersion", deployments.LastInput!.TemplateJson);
        Assert.Contains("\"env\"", deployments.LastInput.ParametersJson);
    }

    [Fact]
    public async Task DeployArmTemplateFallsBackToLatestApprovedPlanTemplate()
    {
        var deployments = new CapturingDeploymentService();
        var planStore = new PlanStore();
        var tools = new AzureCrudTools(null!, new CapturingGenericResourceService(), deployments, planStore, new QuestionStore(), "session-1", "sub");
        var template = JsonSerializer.Deserialize<Dictionary<string, object?>>("""{ "contentVersion": "1.0.0.0", "resources": [] }""");
        var parameters = JsonSerializer.Deserialize<Dictionary<string, object?>>("""{ "env": { "value": "test" } }""");

        var planJson = tools.CreatePlan(
            title: "Create test resources",
            operations:
            [
                new Dictionary<string, object?>
                {
                    ["action"] = "create",
                    ["resource_type"] = "Microsoft.Storage/storageAccounts",
                    ["resource_name"] = "store123",
                    ["resource_group"] = "rg1",
                    ["details"] = new Dictionary<string, object?> { ["location"] = "eastus" }
                }
            ],
            template_json: template,
            parameters_json: parameters,
            resource_group_name: "rg1",
            location: "eastus",
            deployment_name: "dep-from-plan");

        Assert.Contains("\"ok\":true", planJson);

        var deployJson = await tools.DeployArmTemplateAsync();

        Assert.Contains("\"ok\":true", deployJson);
        Assert.Equal(1, deployments.ValidateCalls);
        Assert.Equal(1, deployments.CreateCalls);
        Assert.Equal("rg1", deployments.LastInput!.ResourceGroupName);
        Assert.Equal("eastus", deployments.LastInput.Location);
        Assert.Equal("dep-from-plan", deployments.LastInput.DeploymentName);
        Assert.Contains("contentVersion", deployments.LastInput.TemplateJson);
        Assert.Contains("\"env\"", deployments.LastInput.ParametersJson);
    }

    [Fact]
    public async Task DeployArmTemplateWrapsResourceGroupResourcesWhenPlanCreatesResourceGroup()
    {
        var deployments = new CapturingDeploymentService();
        var planStore = new PlanStore();
        var tools = new AzureCrudTools(null!, new CapturingGenericResourceService(), deployments, planStore, new QuestionStore(), "session-1", "sub");
        var template = JsonSerializer.Deserialize<Dictionary<string, object?>>("""
            {
              "contentVersion": "1.0.0.0",
              "resources": [
                {
                  "type": "Microsoft.Resources/resourceGroups",
                  "apiVersion": "2022-09-01",
                  "name": "rg-new",
                  "location": "eastus"
                },
                {
                  "type": "Microsoft.Storage/storageAccounts",
                  "apiVersion": "2023-01-01",
                  "name": "storagetest123",
                  "location": "eastus",
                  "sku": { "name": "Standard_LRS" },
                  "kind": "StorageV2"
                }
              ]
            }
            """);

        tools.CreatePlan(
            title: "Create new resource group and storage",
            operations:
            [
                new Dictionary<string, object?>
                {
                    ["action"] = "create",
                    ["resource_type"] = "Microsoft.Resources/resourceGroups",
                    ["resource_name"] = "rg-new",
                    ["details"] = new Dictionary<string, object?> { ["location"] = "eastus" }
                },
                new Dictionary<string, object?>
                {
                    ["action"] = "create",
                    ["resource_type"] = "Microsoft.Storage/storageAccounts",
                    ["resource_name"] = "storagetest123",
                    ["resource_group"] = "rg-new",
                    ["details"] = new Dictionary<string, object?> { ["location"] = "eastus" }
                }
            ],
            template_json: template,
            deployment_name: "dep-rg-create");

        var deployJson = await tools.DeployArmTemplateAsync();

        Assert.Contains("\"ok\":true", deployJson);
        Assert.Equal(1, deployments.ValidateCalls);
        Assert.Equal(1, deployments.CreateCalls);
        Assert.Null(deployments.LastInput!.ResourceGroupName);
        Assert.Equal("eastus", deployments.LastInput.Location);
        Assert.Contains("\"type\":\"Microsoft.Resources/deployments\"", deployments.LastInput.TemplateJson);
        Assert.Contains("\"resourceGroup\":\"rg-new\"", deployments.LastInput.TemplateJson);
        Assert.Contains("Microsoft.Storage/storageAccounts", deployments.LastInput.TemplateJson);
    }

    [Fact]
    public async Task DeployArmTemplateReturnsStructuredValidationErrorBeforeDeploy()
    {
        var deployments = new CapturingDeploymentService
        {
            ValidationResult = new ArmDeploymentApplyResult
            {
                Succeeded = false,
                DeploymentName = "dep1",
                ErrorCode = "InvalidTemplate",
                ErrorMessage = "Missing required property.",
                HttpStatus = 400
            }
        };
        var tools = new AzureCrudTools(null!, new CapturingGenericResourceService(), deployments, new PlanStore(), new QuestionStore(), "s", "sub");
        var template = JsonSerializer.Deserialize<Dictionary<string, object?>>("""{ "resources": [] }""");

        var json = await tools.DeployArmTemplateAsync(template: template, deploymentName: "dep1");

        Assert.Contains("\"ok\":false", json);
        Assert.Contains("\"invalid_template\"", json);
        Assert.Equal(1, deployments.ValidateCalls);
        Assert.Equal(0, deployments.CreateCalls);
    }

    [Fact]
    public void CreatePlanReturnsStructuredErrorWhenOperationsAreMissing()
    {
        var tools = new AzureCrudTools(null!, new CapturingGenericResourceService(), new CapturingDeploymentService(), new PlanStore(), new QuestionStore(), "s", "sub");

        var json = tools.CreatePlan();

        Assert.Contains("\"ok\":false", json);
        Assert.Contains("\"invalid_plan\"", json);
        Assert.Contains("operations must be a JSON array", json);
    }

    [Fact]
    public void CloudOpsMcpAuditSummaryIgnoresRecoveredDeploymentFailures()
    {
        var store = new CloudOpsMcpAuditStore();
        const string sessionId = "session-1";

        var planJson = AgentResultJson.Serialize(new
        {
            ok = true,
            kind = AgentResultKinds.PlanCreated,
            data = new
            {
                operations = new[]
                {
                    new
                    {
                        action = "create",
                        resource_type = "Microsoft.Storage/storageAccounts",
                        resource_name = "store123",
                        resource_group = "rg1"
                    },
                    new
                    {
                        action = "create",
                        resource_type = "Microsoft.Network/virtualNetworks",
                        resource_name = "vnet1",
                        resource_group = "rg1"
                    }
                }
            },
            message = "Plan created and auto-approved for prototype mode. Execute it now."
        });
        var failedDeployJson = AgentResultJson.Serialize(new
        {
            ok = false,
            kind = AgentResultKinds.DeploymentFailed,
            error = new
            {
                type = "invalid_template",
                message = "Deployment template validation failed. Content: { huge azure payload }",
                http_status = 400
            },
            message = "Deployment template validation failed. Content: { huge azure payload }"
        });
        var successfulMutateJson = AgentResultJson.Serialize(new
        {
            ok = true,
            kind = "resource_mutated",
            data = new
            {
                resource_id = "/subscriptions/sub/resourceGroups/rg1/providers/Microsoft.Storage/storageAccounts/store123"
            }
        });

        store.RecordResult(sessionId, "create_plan", planJson);
        store.RecordResult(sessionId, "deploy_arm_template", failedDeployJson);
        store.RecordResult(sessionId, "create_or_update_resource", successfulMutateJson);
        store.RecordResult(sessionId, "list_resources", AgentResultJson.Serialize(new { ok = true, kind = "resources_listed", data = new { nodes = Array.Empty<object>() } }));

        var summary = store.BuildSummary(sessionId);

        Assert.Equal("Successfully implemented storage account store123, virtual network vnet1. Verification completed.", summary);
        Assert.DoesNotContain("Deployment template validation failed", summary);
        Assert.DoesNotContain("Content:", summary);
    }

    [Fact]
    public async Task CreateOrUpdateResourceAcceptsArbitraryResourceIdAndRawArmFields()
    {
        var generic = new CapturingGenericResourceService();
        var tools = new AzureCrudTools(null!, generic, new CapturingDeploymentService(), new PlanStore(), new QuestionStore(), "s", "sub");
        var properties = JsonSerializer.Deserialize<Dictionary<string, object?>>("""{ "addressSpace": { "addressPrefixes": [ "10.0.0.0/16" ] } }""");
        var sku = JsonSerializer.Deserialize<Dictionary<string, object?>>("""{ "name": "Standard" }""");

        var json = await tools.CreateOrUpdateResourceAsync(
            resourceId: "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Network/virtualNetworks/vnet1",
            apiVersion: "2023-11-01",
            location: "eastus",
            properties: properties,
            tags: new Dictionary<string, string> { ["env"] = "test" },
            sku: sku,
            kind: "network");

        Assert.Contains("\"ok\":true", json);
        Assert.Contains("addressSpace", generic.PropertiesJson);
        Assert.Contains("Standard", generic.SkuJson);
        Assert.Equal("test", generic.Tags!["env"]);
        Assert.Equal("network", generic.Kind);
    }

    private sealed class CapturingDeploymentService : IArmDeploymentService
    {
        public int ValidateCalls { get; private set; }
        public int CreateCalls { get; private set; }
        public ArmDeploymentApplyInput? LastInput { get; private set; }
        public ArmDeploymentApplyResult? ValidationResult { get; init; }

        public Task<ArmDeploymentApplyResult> ValidateAsync(ArmDeploymentApplyInput input, CancellationToken cancellationToken = default)
        {
            ValidateCalls++;
            LastInput = input;
            return Task.FromResult(ValidationResult ?? Success(input));
        }

        public Task<ArmDeploymentApplyResult> CreateOrUpdateAsync(ArmDeploymentApplyInput input, CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            LastInput = input;
            return Task.FromResult(Success(input));
        }

        public Task<ArmDeploymentApplyResult> GetDeploymentAsync(string subscriptionId, string? resourceGroupName, string deploymentName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ArmDeploymentApplyResult { Succeeded = true, DeploymentName = deploymentName });

        private static ArmDeploymentApplyResult Success(ArmDeploymentApplyInput input) =>
            new() { Succeeded = true, DeploymentName = input.DeploymentName, Scope = input.ResourceGroupName };
    }

    private sealed class CapturingGenericResourceService : IArmGenericResourceService
    {
        public string? PropertiesJson { get; private set; }
        public IReadOnlyDictionary<string, string>? Tags { get; private set; }
        public string? SkuJson { get; private set; }
        public string? Kind { get; private set; }

        public Task<GenericResourceOperationResult> GetAsync(string resourceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GenericResourceOperationResult { Succeeded = true, ResourceId = resourceId });

        public Task<GenericResourceOperationResult> CreateOrUpdateAsync(
            string resourceId,
            string location,
            string? propertiesJson,
            IReadOnlyDictionary<string, string>? tags,
            string? skuJson,
            string? kind,
            bool waitForCompletion,
            CancellationToken cancellationToken = default)
        {
            PropertiesJson = propertiesJson;
            Tags = tags;
            SkuJson = skuJson;
            Kind = kind;
            return Task.FromResult(new GenericResourceOperationResult { Succeeded = true, ResourceId = resourceId });
        }

        public Task<GenericResourceOperationResult> DeleteAsync(string resourceId, bool waitForCompletion, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GenericResourceOperationResult { Succeeded = true, ResourceId = resourceId });
    }
}
