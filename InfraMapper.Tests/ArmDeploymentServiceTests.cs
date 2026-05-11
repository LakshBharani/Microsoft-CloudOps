using System.Reflection;
using Azure.ResourceManager.Resources.Models;
using InfraMapper.Models;
using InfraMapper.Services;

namespace InfraMapper.Tests;

public sealed class ArmDeploymentServiceTests
{
    [Fact]
    public void BuildContentOmitsLocationForResourceGroupScope()
    {
        var content = BuildContent(new ArmDeploymentApplyInput
        {
            SubscriptionId = "sub",
            ResourceGroupName = "rg1",
            DeploymentName = "dep1",
            TemplateJson = """{ "resources": [] }""",
            ParametersJson = "{}",
            Location = "eastus"
        }, isSubscriptionScope: false);

        Assert.Null(content.Location);
    }

    [Fact]
    public void BuildContentKeepsLocationForSubscriptionScope()
    {
        var content = BuildContent(new ArmDeploymentApplyInput
        {
            SubscriptionId = "sub",
            DeploymentName = "dep1",
            TemplateJson = """{ "resources": [] }""",
            ParametersJson = "{}",
            Location = "eastus"
        }, isSubscriptionScope: true);

        Assert.Equal("eastus", content.Location?.ToString());
    }

    private static ArmDeploymentContent BuildContent(ArmDeploymentApplyInput input, bool isSubscriptionScope)
    {
        var method = typeof(ArmDeploymentService).GetMethod(
            "BuildContent",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return Assert.IsType<ArmDeploymentContent>(method!.Invoke(null, [input, isSubscriptionScope]));
    }
}
