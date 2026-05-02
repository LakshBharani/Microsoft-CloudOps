using System.Text.Json;
using InfraMapper.Models;

namespace InfraMapper.Services;

internal static class AzureResourceGraphJson
{
    private static readonly JsonSerializerOptions PropertyBagOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static void PopulateFromRow(JsonElement element, ResourceNode node)
    {
        node.Id = element.GetProperty("id").GetString()!;
        node.Name = element.GetProperty("name").GetString()!;
        node.Type = element.GetProperty("type").GetString()!;
        node.ResourceGroup = element.GetProperty("resourceGroup").GetString()!;
        node.Location = element.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.String
            ? loc.GetString() ?? string.Empty
            : string.Empty;

        node.Tags.Clear();
        if (element.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in tags.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    node.Tags[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
        }

        node.Properties.Clear();
        if (element.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(props.GetRawText(), PropertyBagOptions);
            if (parsed != null)
            {
                foreach (var kv in parsed)
                    node.Properties[kv.Key] = kv.Value;
            }
        }

        node.SkuJson = null;
        if (element.TryGetProperty("sku", out var sku) && sku.ValueKind is JsonValueKind.Object)
            node.SkuJson = sku.GetRawText();

        node.Kind = null;
        if (element.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String)
            node.Kind = kind.GetString();
    }

    /// <summary>Identity fields only; leaves Tags/Properties empty for dependency resolution heuristics that do not need ARG payloads.</summary>
    internal static void PopulateSummaryFromRow(JsonElement element, ResourceNode node)
    {
        node.Id = element.GetProperty("id").GetString()!;
        node.Name = element.GetProperty("name").GetString()!;
        node.Type = element.GetProperty("type").GetString()!;
        node.ResourceGroup = element.GetProperty("resourceGroup").GetString()!;
        node.Location = element.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.String
            ? loc.GetString() ?? string.Empty
            : string.Empty;
        node.Tags.Clear();
        node.Properties.Clear();
        if (element.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(props.GetRawText(), PropertyBagOptions);
            if (parsed != null)
            {
                foreach (var kv in parsed)
                    node.Properties[kv.Key] = kv.Value;
            }
        }
        node.SkuJson = null;
        node.Kind = null;
    }
}
