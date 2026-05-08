using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using InfraMapper.Services.Agent.Memory;
using Microsoft.SemanticKernel;

namespace InfraMapper.Services.Agent.Tools;

public sealed class PlannerTools
{
    private readonly PlanStore _planStore;
    private readonly ILessonsStore _lessonsStore;
    private readonly string _sessionId;
    private string _currentIntent = "";

    public PlannerTools(PlanStore planStore, ILessonsStore lessonsStore, string sessionId)
    {
        _planStore = planStore;
        _lessonsStore = lessonsStore;
        _sessionId = sessionId;
    }

    public void BeginPlan(string intent)
    {
        _currentIntent = intent;
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
        // No persistent storage needed in Phase 2; the tool forces the LLM to surface the critique
        // in its chain-of-thought before committing to create_plan.
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
        [Description("Optional human-readable cost estimate")] string? estimatedCostNote = null)
    {
        var parsedOperations = ParseOperations(operations);
        if (parsedOperations is null)
        {
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

        var validationError = ValidateUserNamedResources(parsedOperations);
        if (validationError is not null)
            return validationError;

        var planDataEl = JsonSerializer.SerializeToElement(
            new { title, operations = parsedOperations, risk_level = riskLevel, estimated_cost_note = estimatedCostNote },
            OrchestratorTools.SnakeCaseOpts);

        var planId = _planStore.CreatePlan(_sessionId, planDataEl);

        return JsonSerializer.Serialize(new
        {
            plan_id = planId.ToString(),
            status = "awaiting_user_approval",
            title,
            operations = parsedOperations,
            risk_level = riskLevel,
            estimated_cost_note = estimatedCostNote,
        }, OrchestratorTools.SnakeCaseOpts);
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
        var requestedStorageNames = ExtractRequestedStorageAccountNames(_currentIntent);
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

    private static IReadOnlyList<string> ExtractRequestedStorageAccountNames(string intent)
    {
        if (string.IsNullOrWhiteSpace(intent))
            return Array.Empty<string>();

        var names = new List<string>();
        AddMatches(names, Regex.Matches(
            intent,
            @"\b(?:Create|Update|Deploy)\s+Microsoft\.Storage/storageAccounts\s+""([^""]+)""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

        AddMatches(names, Regex.Matches(
            intent,
            @"""(?:type|resource_type)""\s*:\s*""Microsoft\.Storage/storageAccounts""[\s\S]{0,500}?""(?:name|resource_name)""\s*:\s*""([^""]+)""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

        AddMatches(names, Regex.Matches(
            intent,
            @"""(?:name|resource_name)""\s*:\s*""([^""]+)""[\s\S]{0,500}?""(?:type|resource_type)""\s*:\s*""Microsoft\.Storage/storageAccounts""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

        return names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddMatches(List<string> names, MatchCollection matches)
    {
        foreach (Match match in matches)
        {
            if (match.Groups.Count > 1)
                names.Add(match.Groups[1].Value);
        }
    }
}
