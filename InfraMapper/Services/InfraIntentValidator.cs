using System.Text.Json;
using System.Text.RegularExpressions;
using InfraMapper.Models;

namespace InfraMapper.Services;

public sealed record InfraIntentValidationResult(
    bool IsValid,
    InfraIntentSpec? Spec,
    string? Error,
    string NormalizedJson,
    IReadOnlyList<string> Warnings);

public sealed class InfraIntentValidator
{
    private static readonly Regex StorageAccountNameRegex = new("^[a-z0-9]{3,24}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public InfraIntentValidationResult Validate(string rawMessage, string fallbackSubscriptionId)
    {
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(rawMessage));
            if (!InfraIntentCompiler.LooksLikeIntent(doc.RootElement))
                return new InfraIntentValidationResult(false, null, null, "", []);

            var spec = doc.RootElement.Deserialize<InfraIntentSpec>(JsonOpts);
            if (spec is null)
                return Invalid("InfraIntentSpec JSON is empty.");

            Normalize(spec, fallbackSubscriptionId);
            var warnings = ValidateSpec(spec);
            return new InfraIntentValidationResult(
                true,
                spec,
                null,
                JsonSerializer.Serialize(spec, JsonOpts),
                warnings);
        }
        catch (JsonException)
        {
            return new InfraIntentValidationResult(false, null, null, "", []);
        }
        catch (Exception ex)
        {
            return Invalid(ex.Message);
        }
    }

    internal static string ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return text;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (ch == '\\' && inString)
            {
                escaped = true;
                continue;
            }
            if (ch == '"')
            {
                inString = !inString;
                continue;
            }
            if (inString) continue;

            if (ch == '{') depth++;
            else if (ch == '}' && --depth == 0)
                return text[start..(i + 1)];
        }

        return text[start..];
    }

    private static void Normalize(InfraIntentSpec spec, string fallbackSubscriptionId)
    {
        if (string.IsNullOrWhiteSpace(spec.Scope.SubscriptionId))
            spec.Scope.SubscriptionId = fallbackSubscriptionId;

        if (spec.Constraints is null)
            spec.Constraints = [];
        if (spec.Components is null)
            spec.Components = [];
    }

    private static IReadOnlyList<string> ValidateSpec(InfraIntentSpec spec)
    {
        var warnings = new List<string>();

        Required(spec.Scope.SubscriptionId, "scope.subscriptionId or subscriptionId query parameter is required.");
        Required(spec.Scope.ResourceGroup, "scope.resourceGroup is required.");
        Required(spec.Scope.Location, "scope.location is required.");

        if (spec.Components.Count == 0)
            throw new InvalidOperationException("components must include at least one item.");

        var noCompute = IsNoCompute(spec);
        foreach (var component in spec.Components)
        {
            if (noCompute && IsBlockedNoComputeComponent(component, out var blocked))
                throw new InvalidOperationException($"noCompute blocks requested resource '{component.Name}' ({blocked}).");

            if (IsStorageAccount(component) &&
                !string.IsNullOrWhiteSpace(component.Name) &&
                !StorageAccountNameRegex.IsMatch(component.Name))
                warnings.Add($"Storage account '{component.Name}' should be lowercase alphanumeric and 3-24 characters; ask for or generate a valid ARM name before deployment.");
        }

        return warnings;
    }

    private static bool IsNoCompute(InfraIntentSpec spec)
    {
        if (!spec.Constraints.TryGetValue("noCompute", out var noComputeEl))
            return false;
        return noComputeEl.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => bool.TryParse(noComputeEl.GetString(), out var parsed) && parsed,
            _ => false
        };
    }

    private static bool IsBlockedNoComputeComponent(InfraComponentSpec component, out string blocked)
    {
        var kind = component.Kind ?? "";
        var type = GetString(component, "type") ?? kind;
        blocked = type;

        if (MatchesBlockedType(type))
            return true;

        var normalizedKind = kind.Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .ToLowerInvariant();
        blocked = kind;
        return normalizedKind is "vm" or "virtualmachine" or "webapp" or "appservice" or "database" or "sql" or "publicip" or "publicipaddress" or "aks" or "kubernetes";
    }

    private static bool MatchesBlockedType(string type) =>
        StartsWith(type, "Microsoft.Compute/") ||
        EqualsType(type, "Microsoft.Web/sites") ||
        StartsWith(type, "Microsoft.ContainerService/") ||
        StartsWith(type, "Microsoft.Sql/") ||
        StartsWith(type, "Microsoft.DBfor") ||
        EqualsType(type, "Microsoft.Network/publicIPAddresses");

    private static bool IsStorageAccount(InfraComponentSpec component)
    {
        var kind = component.Kind ?? "";
        var type = GetString(component, "type") ?? "";
        return kind.Equals("storageAccount", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("Microsoft.Storage/storageAccounts", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetString(InfraComponentSpec component, string name) =>
        component.TryGetString(name, out var value) ? value : null;

    private static bool StartsWith(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool EqualsType(string value, string type) =>
        value.Equals(type, StringComparison.OrdinalIgnoreCase);

    private static void Required(string value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(message);
    }

    private static InfraIntentValidationResult Invalid(string error) =>
        new(false, null, error, "", []);
}
