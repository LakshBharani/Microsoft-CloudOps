using System.Text.Json;
using System.Text.RegularExpressions;
using InfraMapper.Services.Agent.Tools;

namespace InfraMapper.Services.Agent.State;

public sealed record ValidatorError(string ErrorType, string Message, object? Extra = null);

public static class PlanStructuralValidator
{
    public static ValidatorError? Validate(
        AgentTaskState? state,
        PlanOperationDto[] operations,
        string? templateJson)
    {
        var coverageError = ValidateCoverage(state, operations, templateJson);
        if (coverageError is not null) return coverageError;

        var templateError = ValidateTemplateShape(operations, templateJson);
        if (templateError is not null) return templateError;

        var scopeError = ValidateDeploymentScope(templateJson);
        if (scopeError is not null) return scopeError;

        var parityError = ValidateOperationsTemplateParity(operations, templateJson);
        if (parityError is not null) return parityError;

        var azurePropsError = ValidateAzureProps(operations, templateJson);
        if (azurePropsError is not null) return azurePropsError;

        var nameError = ValidateNames(operations);
        if (nameError is not null) return nameError;

        return null;
    }

    private static ValidatorError? ValidateDeploymentScope(string? templateJson)
    {
        if (string.IsNullOrWhiteSpace(templateJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(templateJson);
            foreach (var resource in EnumerateDirectResources(doc.RootElement))
            {
                var type = GetString(resource, "type") ?? "";
                if (!type.Equals("Microsoft.Resources/deployments", StringComparison.OrdinalIgnoreCase))
                    continue;

                var nestedTemplate = GetNested(resource, "properties", "template");
                if (nestedTemplate is null || !NestedTemplateHasResourceGroupResources(nestedTemplate.Value))
                    continue;

                if (!resource.TryGetProperty("resourceGroup", out var resourceGroupEl) ||
                    resourceGroupEl.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(resourceGroupEl.GetString()))
                {
                    return new ValidatorError(
                        "invalid_deployment_scope",
                        $"Nested deployment '{GetString(resource, "name")}' contains resource-group resources but is missing top-level resourceGroup.");
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static ValidatorError? ValidateCoverage(
        AgentTaskState? state,
        PlanOperationDto[] operations,
        string? templateJson)
    {
        if (state is null) return null;
        var required = state.RequiredComponents;
        if (required.Count == 0) return null;

        var searchable = string.Join("\n", operations.Select(o => $"{o.ResourceType}\n{o.ResourceName}\n{o.Details}"));
        if (!string.IsNullOrWhiteSpace(templateJson))
            searchable += "\n" + templateJson;

        var inlineSubnetParents = ExtractInlineSubnetParents(templateJson);

        var missing = required
            .Where(c => !IsCovered(c, searchable, inlineSubnetParents))
            .Select(c => c.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (missing.Length == 0) return null;

        return new ValidatorError(
            "missing_intent_components",
            $"The plan dropped component names from the original intent JSON: {string.Join(", ", missing)}. " +
            "Recreate the plan from the original JSON as source of truth, not only the computed diff. " +
            "Do NOT ask the user whether to include them — the intent JSON requires them. " +
            "Add full ARM resource definitions for the missing components and call create_plan again.",
            new
            {
                missing_components = missing,
                suggestion = "Include operations and template_json resources for every requested component. " +
                             "For subnets, either declare a separate Microsoft.Network/virtualNetworks/subnets resource named '{vnet}/{subnet}' " +
                             "or inline them under the parent VNet's properties.subnets[] with name '{subnet}'. " +
                             "For NSG-attached subnets, include properties.networkSecurityGroup.id referencing the NSG. " +
                             "Do not call ask_clarifying_question for this error."
            });
    }

    private static bool IsCovered(
        RequiredComponent component,
        string searchable,
        IReadOnlyDictionary<string, HashSet<string>> inlineSubnetParents)
    {
        if (searchable.Contains(component.Name, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(component.ParentName))
        {
            var childName = component.Name.Contains('/')
                ? component.Name[(component.Name.IndexOf('/') + 1)..]
                : component.Name;

            if (inlineSubnetParents.TryGetValue(component.ParentName, out var children) &&
                children.Contains(childName, StringComparer.OrdinalIgnoreCase))
                return true;

            if (searchable.Contains(component.ParentName, StringComparison.OrdinalIgnoreCase) &&
                searchable.Contains(childName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static IReadOnlyDictionary<string, HashSet<string>> ExtractInlineSubnetParents(string? templateJson)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(templateJson)) return map;

        try
        {
            using var doc = JsonDocument.Parse(templateJson);
            foreach (var resource in EnumerateAllResources(doc.RootElement))
            {
                var type = GetString(resource, "type") ?? "";
                if (!type.Equals("Microsoft.Network/virtualNetworks", StringComparison.OrdinalIgnoreCase))
                    continue;
                var parentName = GetString(resource, "name");
                if (string.IsNullOrWhiteSpace(parentName)) continue;

                var subnets = GetNested(resource, "properties", "subnets");
                if (subnets is null || subnets.Value.ValueKind != JsonValueKind.Array) continue;

                var children = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var subnet in subnets.Value.EnumerateArray())
                {
                    var subnetName = GetString(subnet, "name");
                    if (!string.IsNullOrWhiteSpace(subnetName))
                        children.Add(subnetName!);
                }
                if (children.Count > 0)
                    map[parentName!] = children;
            }
        }
        catch
        {
        }

        return map;
    }

    private static ValidatorError? ValidateTemplateShape(PlanOperationDto[] operations, string? templateJson)
    {
        if (operations.All(o => string.Equals(o.Action, "Delete", StringComparison.OrdinalIgnoreCase)))
            return null;

        if (string.IsNullOrWhiteSpace(templateJson))
            return new ValidatorError(
                "missing_template_json",
                "Non-delete plans must include template_json: a complete deployable ARM template JSON string. Do not rely on Executor to invent Azure resource properties.");

        try
        {
            using var doc = JsonDocument.Parse(templateJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("resources", out var resources) ||
                resources.ValueKind != JsonValueKind.Array)
            {
                return new ValidatorError(
                    "invalid_template_json",
                    "template_json must be an ARM template object with a resources array.");
            }
        }
        catch (JsonException ex)
        {
            return new ValidatorError("invalid_template_json", $"template_json is not valid JSON: {ex.Message}");
        }

        return null;
    }

    private static ValidatorError? ValidateOperationsTemplateParity(PlanOperationDto[] operations, string? templateJson)
    {
        if (string.IsNullOrWhiteSpace(templateJson)) return null;

        var templateNames = ExtractTemplateResourceNames(templateJson);
        if (templateNames.Count == 0) return null;

        var inlineSubnets = ExtractInlineSubnetParents(templateJson);
        var inlineFullNames = inlineSubnets
            .SelectMany(kv => kv.Value.Select(child => $"{kv.Key}/{child}"))
            .ToList();

        var operationNamesNonDelete = operations
            .Where(o => !string.Equals(o.Action, "Delete", StringComparison.OrdinalIgnoreCase))
            .Where(o => !string.Equals(o.ResourceType, "Microsoft.Resources/resourceGroups", StringComparison.OrdinalIgnoreCase))
            .Select(o => NormalizeName(o.ResourceName))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingFromTemplate = operationNamesNonDelete
            .Where(n => !templateNames.Contains(n, StringComparer.OrdinalIgnoreCase) &&
                        !inlineFullNames.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (missingFromTemplate.Length > 0)
            return new ValidatorError(
                "operations_template_mismatch",
                $"Operations reference resources not present in template_json: {string.Join(", ", missingFromTemplate)}. Add them to template_json or remove them from operations.",
                new { missing_from_template = missingFromTemplate });

        var inlineChildOnlyNames = inlineFullNames
            .Select(n => n.Contains('/') ? n[(n.IndexOf('/') + 1)..] : n)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var wrapperNames = ExtractTemplateWrapperResourceNames(templateJson);
        var missingFromOperations = templateNames
            .Where(n => !wrapperNames.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Where(n => !operationNamesNonDelete.Contains(n) &&
                        !inlineChildOnlyNames.Contains(n) &&
                        !string.IsNullOrWhiteSpace(n))
            .ToArray();

        if (missingFromOperations.Length > 0)
            return new ValidatorError(
                "operations_template_mismatch",
                $"template_json contains resources not listed in operations: {string.Join(", ", missingFromOperations)}. Add matching operations or remove the resources from template_json.",
                new { missing_from_operations = missingFromOperations });

        return null;
    }

    private static HashSet<string> ExtractTemplateWrapperResourceNames(string templateJson)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(templateJson);
            foreach (var resource in EnumerateAllResources(doc.RootElement))
            {
                var type = GetString(resource, "type") ?? "";
                if (!type.Equals("Microsoft.Resources/resourceGroups", StringComparison.OrdinalIgnoreCase) &&
                    !type.Equals("Microsoft.Resources/deployments", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = GetString(resource, "name");
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(NormalizeName(name!));
            }
        }
        catch
        {
        }

        return names;
    }

    private static ValidatorError? ValidateAzureProps(PlanOperationDto[] operations, string? templateJson)
    {
        if (string.IsNullOrWhiteSpace(templateJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(templateJson);
            foreach (var resource in EnumerateAllResources(doc.RootElement))
            {
                var type = GetString(resource, "type") ?? "";
                if (type.Equals("Microsoft.Network/virtualNetworks", StringComparison.OrdinalIgnoreCase))
                {
                    var addrSpace = GetNested(resource, "properties", "addressSpace");
                    if (addrSpace is null ||
                        !addrSpace.Value.TryGetProperty("addressPrefixes", out var prefixes) ||
                        prefixes.ValueKind != JsonValueKind.Array ||
                        prefixes.GetArrayLength() == 0)
                        return new ValidatorError(
                            "invalid_template_json",
                            $"VNet '{GetString(resource, "name")}' missing properties.addressSpace.addressPrefixes.");
                }
                else if (type.Equals("Microsoft.Network/virtualNetworks/subnets", StringComparison.OrdinalIgnoreCase))
                {
                    var props = GetNested(resource, "properties");
                    if (props is null) goto subnet_done;
                    var hasPrefix = props.Value.TryGetProperty("addressPrefix", out var pfx) && pfx.ValueKind == JsonValueKind.String;
                    var hasPrefixes = props.Value.TryGetProperty("addressPrefixes", out var pfxs) && pfxs.ValueKind == JsonValueKind.Array && pfxs.GetArrayLength() > 0;
                    if (!hasPrefix && !hasPrefixes)
                        return new ValidatorError(
                            "invalid_template_json",
                            $"Subnet '{GetString(resource, "name")}' missing properties.addressPrefix(es).");
                }
                subnet_done:;
                if (type.Equals("Microsoft.KeyVault/vaults", StringComparison.OrdinalIgnoreCase))
                {
                    if (!resource.TryGetProperty("sku", out var sku) || sku.ValueKind != JsonValueKind.Object)
                        return new ValidatorError(
                            "invalid_template_json",
                            $"Key Vault '{GetString(resource, "name")}' missing top-level sku.");

                    var props = GetNested(resource, "properties");
                    if (props is not null &&
                        props.Value.ValueKind == JsonValueKind.Object &&
                        props.Value.TryGetProperty("sku", out _))
                        return new ValidatorError(
                            "invalid_template_json",
                            $"Key Vault '{GetString(resource, "name")}' has sku under properties; move sku to the resource top level.");
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static bool NestedTemplateHasResourceGroupResources(JsonElement template)
    {
        foreach (var resource in EnumerateAllResources(template))
        {
            var type = GetString(resource, "type") ?? "";
            if (!type.StartsWith("Microsoft.Resources/", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static ValidatorError? ValidateNames(PlanOperationDto[] operations)
    {
        foreach (var op in operations)
        {
            if (string.Equals(op.Action, "Delete", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(op.ResourceType, "Microsoft.Storage/storageAccounts", StringComparison.OrdinalIgnoreCase) &&
                !Regex.IsMatch(op.ResourceName ?? "", "^[a-z0-9]{3,24}$", RegexOptions.CultureInvariant))
            {
                return new ValidatorError(
                    "invalid_resource_name",
                    $"Storage account name '{op.ResourceName}' is invalid. Azure storage account names must be 3-24 characters lowercase alphanumeric.");
            }
        }
        return null;
    }

    private static List<string> ExtractTemplateResourceNames(string templateJson)
    {
        var names = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(templateJson);
            foreach (var resource in EnumerateAllResources(doc.RootElement))
            {
                var name = GetString(resource, "name");
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(NormalizeName(name!));
            }
        }
        catch
        {
        }
        return names;
    }

    private static IEnumerable<JsonElement> EnumerateAllResources(JsonElement root)
    {
        foreach (var resource in EnumerateDirectResources(root))
        {
            yield return resource;

            // Nested deployments: resources[].properties.template.resources[]
            var nestedTemplate = GetNested(resource, "properties", "template");
            if (nestedTemplate is null) continue;
            foreach (var inner in EnumerateAllResources(nestedTemplate.Value))
                yield return inner;
        }
    }

    private static IEnumerable<JsonElement> EnumerateDirectResources(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) yield break;
        if (!root.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var resource in resources.EnumerateArray())
        {
            if (resource.ValueKind != JsonValueKind.Object) continue;
            yield return resource;
        }
    }

    private static JsonElement? GetNested(JsonElement obj, params string[] path)
    {
        var current = obj;
        foreach (var key in path)
        {
            if (current.ValueKind != JsonValueKind.Object) return null;
            if (!current.TryGetProperty(key, out var next)) return null;
            current = next;
        }
        return current;
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var stripped = name.Trim();
        if (stripped.StartsWith("[") && stripped.EndsWith("]"))
        {
            var concatMatch = Regex.Match(stripped, @"'([^']+)'", RegexOptions.CultureInvariant);
            if (concatMatch.Success) return concatMatch.Groups[1].Value;
        }
        return stripped;
    }
}
