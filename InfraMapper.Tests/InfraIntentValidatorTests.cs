using InfraMapper.Services;
using InfraMapper.Services.Agent;

namespace InfraMapper.Tests;

public sealed class InfraIntentValidatorTests
{
    [Fact]
    public void ValidNoComputeIntentBuildsAgentPromptWithoutCompilerContext()
    {
        var result = new InfraIntentValidator().Validate(ValidIntent("""
            [
              { "kind": "storageAccount", "name": "bad-name" },
              { "kind": "networkSecurityGroup", "name": "nsg1", "type": "Microsoft.Network/networkSecurityGroups" },
              { "kind": "routeTable", "name": "rt1", "type": "Microsoft.Network/routeTables" },
              { "kind": "virtualNetwork", "name": "vnet1", "type": "Microsoft.Network/virtualNetworks" },
              { "kind": "subnet", "name": "vnet1/subnet1", "type": "Microsoft.Network/virtualNetworks/subnets" }
            ]
            """), "sub-1");

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("Storage account 'bad-name'"));

        var prompt = AgentService.BuildIntentAgentMessage(result);
        Assert.Contains("deploy_arm_template", prompt);
        Assert.Contains("real ARM resource definitions", prompt);
        Assert.Contains("vnetName/subnetName", prompt);
        Assert.Contains("\"resourceGroup\": \"rg1\"", prompt);
        Assert.DoesNotContain("Deterministic compile context", prompt);
        Assert.Contains("template_json", prompt);
    }

    [Theory]
    [InlineData("vm", "vm1", null)]
    [InlineData("publicIpAddress", "pip1", null)]
    [InlineData("webApp", "app1", null)]
    [InlineData("genericResource", "sql1", "Microsoft.Sql/servers")]
    [InlineData("genericResource", "aks1", "Microsoft.ContainerService/managedClusters")]
    [InlineData("genericResource", "db1", "Microsoft.DBforPostgreSQL/flexibleServers")]
    [InlineData("genericResource", "pip2", "Microsoft.Network/publicIPAddresses")]
    public void NoComputeRejectsExplicitBlockedResources(string kind, string name, string? type)
    {
        var component = type is null
            ? $$"""{ "kind": "{{kind}}", "name": "{{name}}" }"""
            : $$"""{ "kind": "{{kind}}", "name": "{{name}}", "type": "{{type}}" }""";

        var result = new InfraIntentValidator().Validate(ValidIntent($"[ {component} ]"), "sub-1");

        Assert.False(result.IsValid);
        Assert.Contains("noCompute blocks requested resource", result.Error);
    }

    [Fact]
    public void MissingRequiredScopeFailsBeforeAgent()
    {
        var json = """
            {
              "schemaVersion": "1.0",
              "intent": "storage",
              "scope": { "subscriptionId": "sub-1", "location": "eastus" },
              "constraints": { "noCompute": true },
              "components": [ { "kind": "storageAccount", "name": "store123" } ]
            }
            """;

        var result = new InfraIntentValidator().Validate(json, "");

        Assert.False(result.IsValid);
        Assert.Equal("scope.resourceGroup is required.", result.Error);
    }

    private static string ValidIntent(string componentsJson) => $$"""
        {
          "schemaVersion": "1.0",
          "intent": "student safe network lab",
          "scope": {
            "subscriptionId": "sub-1",
            "resourceGroup": "rg1",
            "location": "eastus"
          },
          "constraints": {
            "noCompute": true,
            "studentSafe": true,
            "tags": { "env": "test" }
          },
          "components": {{componentsJson}}
        }
        """;
}
