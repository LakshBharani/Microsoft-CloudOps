using System.Text.Json;
using System.Text.RegularExpressions;

namespace InfraMapper.Services.Agent.State;

public static class IntentParser
{
    public static IntentParseResult Parse(string text, string fallbackSubscriptionId = "")
    {
        var json = ExtractFirstJsonObject(text);
        if (string.IsNullOrWhiteSpace(json))
            return Empty(fallbackSubscriptionId);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Empty(fallbackSubscriptionId);

            var subscriptionId = TryGetNestedString(root, "scope", "subscriptionId") ?? fallbackSubscriptionId ?? "";
            var resourceGroup = TryGetNestedString(root, "scope", "resourceGroup");
            var location = TryGetNestedString(root, "scope", "location");
            var tags = ExtractTags(root);
            var (studentSafe, noCompute) = ExtractConstraintFlags(root);
            var components = ExtractComponents(root);

            return new IntentParseResult(
                OriginalIntentJson: json,
                SubscriptionId: subscriptionId,
                ResourceGroup: resourceGroup,
                Location: location,
                StudentSafe: studentSafe,
                NoCompute: noCompute,
                RequiredComponents: components,
                RequiredTags: tags);
        }
        catch
        {
            return Empty(fallbackSubscriptionId);
        }
    }

    private static IntentParseResult Empty(string fallbackSubscriptionId) => new(
        OriginalIntentJson: null,
        SubscriptionId: fallbackSubscriptionId ?? "",
        ResourceGroup: null,
        Location: null,
        StudentSafe: false,
        NoCompute: false,
        RequiredComponents: Array.Empty<RequiredComponent>(),
        RequiredTags: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static IReadOnlyList<RequiredComponent> ExtractComponents(JsonElement root)
    {
        var list = new List<RequiredComponent>();

        var scopeRg = TryGetNestedString(root, "scope", "resourceGroup");
        if (!string.IsNullOrWhiteSpace(scopeRg))
            list.Add(new RequiredComponent(scopeRg!, "resourceGroup", "Microsoft.Resources/resourceGroups", ParentName: null));

        if (!root.TryGetProperty("components", out var components) || components.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var component in components.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.Object) continue;
            var name = GetString(component, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var kind = GetString(component, "kind") ?? "";
            if (list.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) &&
                              string.Equals(c.ResourceTypeHint, ResourceTypeForKind(kind), StringComparison.OrdinalIgnoreCase)))
                continue;
            list.Add(new RequiredComponent(name!, kind, ResourceTypeForKind(kind), ParentName: null));

            if (component.TryGetProperty("subnets", out var subnets) && subnets.ValueKind == JsonValueKind.Array)
            {
                foreach (var subnet in subnets.EnumerateArray())
                {
                    if (subnet.ValueKind != JsonValueKind.Object) continue;
                    var subnetName = GetString(subnet, "name");
                    if (string.IsNullOrWhiteSpace(subnetName)) continue;
                    list.Add(new RequiredComponent(
                        Name: $"{name}/{subnetName}",
                        Kind: "subnet",
                        ResourceTypeHint: "Microsoft.Network/virtualNetworks/subnets",
                        ParentName: name));
                }
            }
        }

        return list;
    }

    private static string ResourceTypeForKind(string kind) => kind.Trim().ToLowerInvariant() switch
    {
        "storageaccount" => "Microsoft.Storage/storageAccounts",
        "webapp" => "Microsoft.Web/sites",
        "appserviceplan" => "Microsoft.Web/serverfarms",
        "virtualnetwork" => "Microsoft.Network/virtualNetworks",
        "subnet" => "Microsoft.Network/virtualNetworks/subnets",
        "networksecuritygroup" or "nsg" => "Microsoft.Network/networkSecurityGroups",
        "publicip" or "publicipaddress" => "Microsoft.Network/publicIPAddresses",
        "keyvault" => "Microsoft.KeyVault/vaults",
        "resourcegroup" => "Microsoft.Resources/resourceGroups",
        "sqlserver" => "Microsoft.Sql/servers",
        "sqldatabase" => "Microsoft.Sql/servers/databases",
        "vm" or "virtualmachine" => "Microsoft.Compute/virtualMachines",
        "function" or "functionapp" => "Microsoft.Web/sites",
        "aks" or "kubernetescluster" => "Microsoft.ContainerService/managedClusters",
        _ => ""
    };

    private static IReadOnlyDictionary<string, string> ExtractTags(JsonElement root)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddTagsFrom(root, "scope", tags);
        AddTagsFrom(root, "constraints", tags);
        return tags;
    }

    private static void AddTagsFrom(JsonElement root, string parent, Dictionary<string, string> tags)
    {
        if (!root.TryGetProperty(parent, out var section) || section.ValueKind != JsonValueKind.Object)
            return;
        if (!section.TryGetProperty("tags", out var tagsEl) || tagsEl.ValueKind != JsonValueKind.Object)
            return;
        foreach (var prop in tagsEl.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
                tags[prop.Name] = prop.Value.GetString() ?? "";
        }
    }

    private static (bool studentSafe, bool noCompute) ExtractConstraintFlags(JsonElement root)
    {
        var studentSafe = false;
        var noCompute = false;
        if (root.TryGetProperty("constraints", out var constraints) && constraints.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in constraints.EnumerateObject())
            {
                var n = prop.Name.ToLowerInvariant();
                var v = prop.Value;
                if (v.ValueKind == JsonValueKind.True)
                {
                    if (n.Contains("studentsafe")) studentSafe = true;
                    if (n.Contains("nocompute")) noCompute = true;
                }
                else if (v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString() ?? "";
                    if (s.Contains("student", StringComparison.OrdinalIgnoreCase)) studentSafe = true;
                    if (s.Contains("no-compute", StringComparison.OrdinalIgnoreCase) ||
                        s.Contains("nocompute", StringComparison.OrdinalIgnoreCase)) noCompute = true;
                }
            }
        }
        var raw = root.GetRawText();
        if (raw.Contains("student-safe", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("studentSafe", StringComparison.OrdinalIgnoreCase))
            studentSafe = true;
        if (raw.Contains("no-compute", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("noCompute", StringComparison.OrdinalIgnoreCase))
            noCompute = true;
        return (studentSafe, noCompute);
    }

    private static string? TryGetNestedString(JsonElement root, string parent, string child)
    {
        if (!root.TryGetProperty(parent, out var p) || p.ValueKind != JsonValueKind.Object)
            return null;
        return GetString(p, child);
    }

    private static string? GetString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            return null;
        var s = v.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public static string? ExtractFirstJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        if (start < 0) return null;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return text[start..(i + 1)];
            }
        }
        return null;
    }

    public static string ComputeHash(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var normalized = Regex.Replace(text, @"\s+", "");
        var bytes = System.Text.Encoding.UTF8.GetBytes(normalized);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
