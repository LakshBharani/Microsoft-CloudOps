using System.Security.Cryptography;
using System.Text;
using InfraMapper.Models;

namespace InfraMapper.Services;

public static class DeploymentContentHasher
{
    public static string Compute(ArmDeploymentApplyInput input)
    {
        var sb = new StringBuilder();
        sb.AppendLine(input.SubscriptionId.Trim());
        sb.AppendLine((input.ResourceGroupName ?? string.Empty).Trim());
        sb.AppendLine(input.DeploymentName.Trim());
        sb.AppendLine((input.Mode ?? string.Empty).Trim());
        sb.AppendLine((input.Location ?? string.Empty).Trim());
        sb.AppendLine(input.TemplateJson);
        sb.AppendLine(input.ParametersJson ?? string.Empty);
        sb.AppendLine(input.WaitForCompletion ? "1" : "0");

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
