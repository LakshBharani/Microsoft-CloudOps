using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InfraMapper.Models;

namespace InfraMapper.Services;

public static class ResourceMutationContentHasher
{
    public static string Compute(ResourceMutationManifestRequest m)
    {
        var tagsJson = m.Tags == null || m.Tags.Count == 0
            ? ""
            : JsonSerializer.Serialize(m.Tags.OrderBy(kv => kv.Key, StringComparer.Ordinal));

        var sb = new StringBuilder();
        sb.AppendLine(m.ResourceId.Trim());
        sb.AppendLine(m.Operation.ToString());
        sb.AppendLine((m.Location ?? string.Empty).Trim());
        sb.AppendLine(m.PropertiesJson ?? string.Empty);
        sb.AppendLine(tagsJson);
        sb.AppendLine(m.SkuJson ?? string.Empty);
        sb.AppendLine(m.Kind ?? string.Empty);
        sb.AppendLine(m.WaitForCompletion ? "1" : "0");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }
}
