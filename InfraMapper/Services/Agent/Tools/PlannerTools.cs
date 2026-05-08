using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using InfraMapper.Services.Agent.Memory;
using InfraMapper.Services.Agent.State;
using Microsoft.SemanticKernel;

namespace InfraMapper.Services.Agent.Tools;

public sealed class PlannerTools
{
    private readonly PlanStore _planStore;
    private readonly ILessonsStore _lessonsStore;
    private readonly string _sessionId;
    private AgentTaskState? _taskState;
    private string _currentIntent = "";

    public PlannerTools(PlanStore planStore, ILessonsStore lessonsStore, string sessionId)
    {
        _planStore = planStore;
        _lessonsStore = lessonsStore;
        _sessionId = sessionId;
    }

    public void SyncWithTaskState(AgentTaskState state) => _taskState = state;

    public void BeginPlan(string intent)
    {
        _currentIntent = intent ?? "";
    }

    [KernelFunction("get_lessons")]
    [Description("Retrieve relevant lessons from past deployments for the given resource types. " +
                 "Call this BEFORE drafting to avoid repeating known mistakes.")]
    public string GetLessons(
        [Description("Azure resource types to look up lessons for (e.g. ['Microsoft.Storage/storageAccounts'])")] string[] resourceTypes)
    {
        var lessons = _lessonsStore.Query(resourceTypes);
        if (lessons.Count == 0)
            return JsonSerializer.Serialize(new { lessons = Array.Empty<object>(), message = "No lessons recorded for these resource types yet." });

        return JsonSerializer.Serialize(new { lessons });
    }

    [KernelFunction("record_critique")]
    [Description("REQUIRED STEP 2 OF 3: Record your critique of the ARM template draft. " +
                 "Analyze naming conventions, required dependencies, region/SKU compatibility, " +
                 "security, and missing required properties. You MUST call this before create_plan.")]
    public string RecordCritique(
        [Description("Detailed critique covering naming, dependencies, region/SKU, security, missing properties, and ordering issues")] string analysis)
    {
        return "Critique recorded. Now revise your ARM template to address every issue you identified, " +
               "then call create_plan with the improved version.";
    }

    [KernelFunction("create_plan")]
    [Description("REQUIRED STEP 3 OF 3: Submit the final revised deployment plan after self-critique. " +
                 "Call this ONLY after record_critique. Returns plan JSON that must be passed back verbatim.")]
    public string CreatePlan(
        [Description("Short descriptive title for this deployment plan")] string title,
        [Description("Complete list of Azure operations to perform as a JSON array. Each item must have action, resource_type, resource_name, resource_group, and details.")] JsonElement operations,
        [Description("Risk level: Low, Medium, or High")] string riskLevel = "Medium",
        [Description("Optional human-readable cost estimate")] string? estimatedCostNote = null,
        [Description("Full deployable ARM template JSON for all Create/Update/Deploy operations. Required unless the plan is delete-only or clarification-only. Pass a JSON object or a JSON string.")] JsonElement templateJson = default,
        [Description("ARM parameters JSON. Use {} when no parameters are needed. Pass a JSON object or a JSON string.")] JsonElement parametersJson = default,
        [Description("Resource group for a resource-group-scoped deployment. Leave empty for subscription-scoped templates that create resource groups.")] string? resourceGroupName = null,
        [Description("Deployment location for subscription-scoped deployments, e.g. eastus.")] string? location = null,
        [Description("Optional deployment name. If omitted, Executor will choose one.")] string? deploymentName = null)
    {
        var parsedOperations = ParseOperations(operations);
        if (parsedOperations is null)
        {
            Console.WriteLine($"[PlannerTools] create_plan invalid_operations title={PreviewForLog(title)} operations_kind={operations.ValueKind}");
            return JsonSerializer.Serialize(new
            {
                error = true,
                error_type = "invalid_plan_operations",
                message = "operations must be a JSON array of operation objects, not prose.",
                expected_shape = new[]
                {
                    new
                    {
                        action = "Create",
                        resource_type = "Microsoft.Storage/storageAccounts",
                        resource_name = "examplestore001",
                        resource_group = "rg-example",
                        details = "StorageV2, Standard_LRS, eastus"
                    }
                }
            }, OrchestratorTools.SnakeCaseOpts);
        }

        Console.WriteLine(
            $"[PlannerTools] create_plan title={PreviewForLog(title)} ops={parsedOperations.Length} " +
            $"op_names={FormatForLog(parsedOperations.Select(o => $"{o.ResourceType}:{o.ResourceName}"))} " +
            $"template_names={FormatForLog(ExtractTemplateResourceNamesForLog(templateJson))} " +
            $"resource_group={resourceGroupName ?? ""} location={location ?? ""}");

        var nameChoiceError = ValidateUserNamedResources(parsedOperations);
        if (nameChoiceError is not null)
        {
            Console.WriteLine($"[PlannerTools] create_plan rejected error_type=requires_user_choice result={PreviewForLog(nameChoiceError)}");
            return nameChoiceError;
        }

        var templateJsonText = NormalizeJsonArgument(templateJson);
        var parametersJsonText = NormalizeJsonArgument(parametersJson) ?? "{}";

        var structural = PlanStructuralValidator.Validate(_taskState, parsedOperations, templateJsonText);
        if (structural is not null)
        {
            var result = SerializeValidatorError(structural);
            Console.WriteLine($"[PlannerTools] create_plan rejected error_type={structural.ErrorType} result={PreviewForLog(result)}");
            return result;
        }

        var policy = PlanPolicyValidator.Validate(_taskState, parsedOperations);
        if (policy is not null)
        {
            var result = SerializeValidatorError(policy);
            Console.WriteLine($"[PlannerTools] create_plan rejected error_type={policy.ErrorType} result={PreviewForLog(result)}");
            return result;
        }

        var normalizedRiskLevel = NormalizeRiskLevel(parsedOperations, riskLevel);
        if (!string.Equals(normalizedRiskLevel, riskLevel, StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"[PlannerTools] create_plan normalized_risk from={riskLevel} to={normalizedRiskLevel}");

        if (!string.IsNullOrWhiteSpace(parametersJsonText) && parametersJsonText != "{}")
        {
            try { using var _ = JsonDocument.Parse(parametersJsonText); }
            catch (JsonException ex)
            {
                return JsonSerializer.Serialize(new
                {
                    error = true,
                    error_type = "invalid_parameters_json",
                    message = $"parameters_json is not valid JSON: {ex.Message}"
                }, OrchestratorTools.SnakeCaseOpts);
            }
        }

        var planDataEl = JsonSerializer.SerializeToElement(
            new
            {
                title,
                operations = parsedOperations,
                risk_level = normalizedRiskLevel,
                estimated_cost_note = estimatedCostNote,
                template_json = string.IsNullOrWhiteSpace(templateJsonText) ? null : templateJsonText,
                parameters_json = string.IsNullOrWhiteSpace(parametersJsonText) ? "{}" : parametersJsonText,
                resource_group_name = string.IsNullOrWhiteSpace(resourceGroupName) ? null : resourceGroupName,
                location = string.IsNullOrWhiteSpace(location) ? null : location,
                deployment_name = string.IsNullOrWhiteSpace(deploymentName) ? null : deploymentName
            },
            OrchestratorTools.SnakeCaseOpts);

        var planId = _planStore.CreatePlan(_sessionId, planDataEl);
        if (_taskState is not null)
            _taskState.CandidatePlanId = planId;

        return JsonSerializer.Serialize(new
        {
            plan_id = planId.ToString(),
            status = "awaiting_user_approval",
            title,
            operations = parsedOperations,
            risk_level = normalizedRiskLevel,
            estimated_cost_note = estimatedCostNote,
            template_json = string.IsNullOrWhiteSpace(templateJsonText) ? null : templateJsonText,
            parameters_json = string.IsNullOrWhiteSpace(parametersJsonText) ? "{}" : parametersJsonText,
            resource_group_name = string.IsNullOrWhiteSpace(resourceGroupName) ? null : resourceGroupName,
            location = string.IsNullOrWhiteSpace(location) ? null : location,
            deployment_name = string.IsNullOrWhiteSpace(deploymentName) ? null : deploymentName
        }, OrchestratorTools.SnakeCaseOpts);
    }

    private static string NormalizeRiskLevel(PlanOperationDto[] operations, string riskLevel)
    {
        if (operations.Any(o =>
                string.Equals(o.Action, "Delete", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(o.Action, "Update", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(o.ResourceType, "Microsoft.Resources/resourceGroups", StringComparison.OrdinalIgnoreCase)))
            return "High";

        return string.IsNullOrWhiteSpace(riskLevel) ? "Medium" : riskLevel;
    }

    private static string SerializeValidatorError(ValidatorError error)
    {
        if (error.Extra is null)
            return JsonSerializer.Serialize(new
            {
                error = true,
                error_type = error.ErrorType,
                message = error.Message
            }, OrchestratorTools.SnakeCaseOpts);

        var extraEl = JsonSerializer.SerializeToElement(error.Extra, OrchestratorTools.SnakeCaseOpts);
        var doc = new Dictionary<string, object?>
        {
            ["error"] = true,
            ["error_type"] = error.ErrorType,
            ["message"] = error.Message
        };
        foreach (var prop in extraEl.EnumerateObject())
            doc[prop.Name] = prop.Value.Clone();
        return JsonSerializer.Serialize(doc, OrchestratorTools.SnakeCaseOpts);
    }

    private static string? NormalizeJsonArgument(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => value.GetRawText()
        };
    }

    private static string FormatForLog(IEnumerable<string?> values)
    {
        var filtered = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Take(20)
            .ToArray();
        return filtered.Length == 0 ? "[]" : $"[{string.Join(", ", filtered)}]";
    }

    private static IEnumerable<string> ExtractTemplateResourceNamesForLog(JsonElement templateJson)
    {
        var text = NormalizeJsonArgument(templateJson);
        if (string.IsNullOrWhiteSpace(text)) yield break;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch
        {
            yield break;
        }

        using (doc)
        {
            foreach (var name in EnumerateTemplateResourceNamesForLog(doc.RootElement))
                yield return name;
        }
    }

    private static IEnumerable<string> EnumerateTemplateResourceNamesForLog(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) yield break;
        if (!root.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var resource in resources.EnumerateArray())
        {
            if (resource.ValueKind != JsonValueKind.Object) continue;
            var type = GetString(resource, "type") ?? "";
            var name = GetString(resource, "name") ?? "";
            if (!string.IsNullOrWhiteSpace(type) || !string.IsNullOrWhiteSpace(name))
                yield return $"{type}:{name}";

            var nestedTemplate = GetNestedForLog(resource, "properties", "template");
            if (nestedTemplate is null) continue;
            foreach (var nestedName in EnumerateTemplateResourceNamesForLog(nestedTemplate.Value))
                yield return nestedName;
        }
    }

    private static JsonElement? GetNestedForLog(JsonElement obj, params string[] path)
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

    private static string PreviewForLog(string? value)
    {
        const int max = 500;
        if (string.IsNullOrWhiteSpace(value)) return "";
        var normalized = Regex.Replace(value, "\\s+", " ").Trim();
        return normalized.Length <= max ? normalized : normalized[..max] + "...";
    }

    private static PlanOperationDto[]? ParseOperations(JsonElement operations)
    {
        try
        {
            if (operations.ValueKind == JsonValueKind.String)
            {
                var json = operations.GetString();
                if (string.IsNullOrWhiteSpace(json))
                    return null;

                using var doc = JsonDocument.Parse(json);
                return ParseOperationsArray(doc.RootElement);
            }

            return ParseOperationsArray(operations);
        }
        catch
        {
            return null;
        }
    }

    private static PlanOperationDto[]? ParseOperationsArray(JsonElement operations)
    {
        if (operations.ValueKind != JsonValueKind.Array)
            return null;

        var result = new List<PlanOperationDto>();
        foreach (var operation in operations.EnumerateArray())
        {
            if (operation.ValueKind != JsonValueKind.Object)
                return null;

            var action = GetString(operation, "action") ?? "Create";
            var resourceType = GetString(operation, "resource_type") ?? GetString(operation, "resourceType");
            var resourceName = GetString(operation, "resource_name") ?? GetString(operation, "resourceName");
            if (string.IsNullOrWhiteSpace(resourceType) || string.IsNullOrWhiteSpace(resourceName))
                return null;

            result.Add(new PlanOperationDto(
                action,
                resourceType,
                resourceName,
                GetString(operation, "resource_group") ?? GetString(operation, "resourceGroup"),
                GetString(operation, "details")));
        }

        return result.ToArray();
    }

    private static string? GetString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    private string? ValidateUserNamedResources(PlanOperationDto[] operations)
    {
        var requestedStorageNames = ExtractRequestedStorageAccountNames();
        if (requestedStorageNames.Count == 0)
            return null;

        foreach (var requestedName in requestedStorageNames)
        {
            if (!IsValidStorageAccountName(requestedName))
                return RequiresNameChoice(
                    requestedName,
                    $"Storage account name '{requestedName}' is invalid. Azure storage account names must be 3-24 characters and use only lowercase letters and numbers; hyphens are not allowed.");
        }

        var requestedSet = requestedStorageNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plannedStorageNames = operations
            .Where(IsStorageAccountOperation)
            .Select(o => o.ResourceName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var inventedNames = plannedStorageNames
            .Where(name => !requestedSet.Contains(name))
            .ToArray();

        if (inventedNames.Length > 0)
            return RequiresNameChoice(
                inventedNames[0],
                $"The plan changed the user-supplied storage account name instead of asking for confirmation. User requested: {string.Join(", ", requestedStorageNames)}. Planned: {string.Join(", ", plannedStorageNames)}. Ask the user to choose a valid replacement name before creating the plan.");

        return null;
    }

    private IReadOnlyList<string> ExtractRequestedStorageAccountNames()
    {
        if (_taskState is not null)
        {
            var fromState = _taskState.RequiredComponents
                .Where(c => string.Equals(c.ResourceTypeHint, "Microsoft.Storage/storageAccounts", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (fromState.Length > 0) return fromState;
        }

        if (string.IsNullOrWhiteSpace(_currentIntent))
            return Array.Empty<string>();

        var names = new List<string>();
        AddMatches(names, Regex.Matches(
            _currentIntent,
            @"\b(?:Create|Update|Deploy)\s+Microsoft\.Storage/storageAccounts\s+""([^""]+)""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

        return names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsStorageAccountOperation(PlanOperationDto operation) =>
        string.Equals(operation.ResourceType, "Microsoft.Storage/storageAccounts", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(operation.Action, "Delete", StringComparison.OrdinalIgnoreCase);

    private static bool IsValidStorageAccountName(string name) =>
        Regex.IsMatch(name, "^[a-z0-9]{3,24}$", RegexOptions.CultureInvariant);

    private static string RequiresNameChoice(string invalidName, string message) =>
        JsonSerializer.Serialize(new
        {
            error = true,
            error_type = "requires_user_choice",
            category = "name_correction",
            resource_type = "Microsoft.Storage/storageAccounts",
            invalid_name = invalidName,
            message,
            prompt = "Choose a valid Azure storage account name.",
            options = new[]
            {
                new
                {
                    label = "Enter new name",
                    value = "custom_name",
                    description = "Provide a 3-24 character lowercase alphanumeric storage account name."
                },
                new
                {
                    label = "Cancel storage account",
                    value = "cancel_resource",
                    description = "Do not create this storage account."
                }
            },
            allow_custom = true,
            suggestion = "Call ask_clarifying_question. Do not invent a replacement name."
        }, OrchestratorTools.SnakeCaseOpts);

    private static void AddMatches(List<string> names, MatchCollection matches)
    {
        foreach (Match match in matches)
        {
            if (match.Groups.Count > 1)
                names.Add(match.Groups[1].Value);
        }
    }
}
