using System.Text.Json;
using InfraMapper.Models;
using InfraMapper.Services.Agent.Tools;

namespace InfraMapper.Services;

public sealed class CompiledInfraIntent
{
    public DesiredStateSpec DesiredState { get; init; } = new();
    public PlanOperationDto[] Operations { get; init; } = [];
    public string TemplateJson { get; init; } = "";
    public string DeploymentName { get; init; } = "";
    public string Location { get; init; } = "";
    public string? ResourceGroupName { get; init; }
    public List<string> Warnings { get; init; } = [];
}

public sealed class InfraIntentCompiler
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static bool LooksLikeIntent(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        (root.TryGetProperty("components", out _) || root.TryGetProperty("intent", out _));

    public CompiledInfraIntent Compile(InfraIntentSpec spec, string fallbackSubscriptionId)
    {
        var location = Required(spec.Scope.Location, "scope.location");
        var resourceGroup = Required(spec.Scope.ResourceGroup, "scope.resourceGroup");
        var subscriptionId = string.IsNullOrWhiteSpace(spec.Scope.SubscriptionId)
            ? fallbackSubscriptionId
            : spec.Scope.SubscriptionId;

        if (string.IsNullOrWhiteSpace(subscriptionId))
            throw new InvalidOperationException("scope.subscriptionId or subscriptionId query parameter is required.");
        if (spec.Components.Count == 0)
            throw new InvalidOperationException("At least one component is required.");

        var tags = ReadTags(spec);
        var desired = new DesiredStateSpec { Scope = [resourceGroup] };
        var nestedResources = new List<object>();
        var operations = new List<PlanOperationDto>();
        var warnings = new List<string>();
        AddDesired(desired, resourceGroup, "Microsoft.Resources/resourceGroups", resourceGroup, location, tags);
        operations.Add(new PlanOperationDto("Create", "Microsoft.Resources/resourceGroups", resourceGroup, resourceGroup,
            "Ensure resource group exists."));

        foreach (var component in spec.Components)
        {
            switch (NormalizeKind(component.Kind))
            {
                case "resourcegroup":
                    if (!string.Equals(component.Name, resourceGroup, StringComparison.OrdinalIgnoreCase))
                        warnings.Add($"resourceGroup component '{component.Name}' ignored; v1 intent manages scope.resourceGroup '{resourceGroup}'.");
                    break;
                case "storageaccount":
                    AddStorageAccount(component, desired, nestedResources, operations, resourceGroup, location, tags);
                    break;
                case "webapp":
                    AddWebApp(component, desired, nestedResources, operations, subscriptionId, resourceGroup, location, tags, warnings);
                    break;
                case "genericresource":
                    AddGenericResource(component, desired, nestedResources, operations, resourceGroup, location, tags);
                    break;
                default:
                    warnings.Add($"Unsupported component kind '{component.Kind}' was ignored.");
                    break;
            }
        }

        var subscriptionResources = new List<object>
        {
            new
            {
                type = "Microsoft.Resources/resourceGroups",
                apiVersion = "2022-09-01",
                name = "[parameters('resourceGroupName')]",
                location,
                tags
            }
        };
        if (nestedResources.Count > 0)
        {
            subscriptionResources.Add(new
            {
                type = "Microsoft.Resources/deployments",
                apiVersion = "2022-09-01",
                name = $"deploy-{SanitizeDeploymentName(resourceGroup)}",
                resourceGroup = "[parameters('resourceGroupName')]",
                dependsOn = new[] { "[resourceId('Microsoft.Resources/resourceGroups', parameters('resourceGroupName'))]" },
                properties = new
                {
                    mode = "Incremental",
                    template = new
                    {
                        schema = "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
                        contentVersion = "1.0.0.0",
                        resources = nestedResources.ToArray()
                    }
                }
            });
        }
        var template = new
        {
            schema = "https://schema.management.azure.com/schemas/2018-05-01/subscriptionDeploymentTemplate.json#",
            contentVersion = "1.0.0.0",
            parameters = new
            {
                resourceGroupName = new
                {
                    type = "string",
                    defaultValue = resourceGroup
                }
            },
            resources = subscriptionResources.ToArray()
        };

        return new CompiledInfraIntent
        {
            DesiredState = desired,
            Operations = operations.ToArray(),
            TemplateJson = JsonSerializer.Serialize(template, JsonOpts).Replace("\"schema\"", "\"$schema\""),
            DeploymentName = $"im-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
            Location = location,
            ResourceGroupName = null,
            Warnings = warnings
        };
    }

    private static void AddStorageAccount(
        InfraComponentSpec component,
        DesiredStateSpec desired,
        List<object> resources,
        List<PlanOperationDto> operations,
        string resourceGroup,
        string location,
        Dictionary<string, string> tags)
    {
        var name = Required(component.Name, "storageAccount.name").ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9]{3,24}$"))
            throw new InvalidOperationException($"Storage account '{name}' must be 3-24 lowercase letters/numbers.");

        var replication = component.TryGetString("replication", out var repl) ? repl : "LRS";
        var skuName = replication.StartsWith("Standard_", StringComparison.OrdinalIgnoreCase)
            ? replication
            : $"Standard_{replication}";
        var publicAccess = component.TryGetBool("publicAccess", out var pa) && pa;
        var properties = new
        {
            minimumTlsVersion = "TLS1_2",
            allowBlobPublicAccess = publicAccess,
            supportsHttpsTrafficOnly = true
        };
        var sku = new { name = skuName };

        resources.Add(new
        {
            type = "Microsoft.Storage/storageAccounts",
            apiVersion = "2023-05-01",
            name,
            location,
            tags,
            sku,
            kind = "StorageV2",
            properties
        });

        AddDesired(desired, name, "Microsoft.Storage/storageAccounts", resourceGroup, location, tags,
            JsonSerializer.Serialize(sku), "StorageV2");
        operations.Add(new PlanOperationDto("Create", "Microsoft.Storage/storageAccounts", name, resourceGroup,
            $"Agent-generated component: storageAccount; SKU {skuName}; publicAccess={publicAccess}."));
    }

    private static void AddWebApp(
        InfraComponentSpec component,
        DesiredStateSpec desired,
        List<object> resources,
        List<PlanOperationDto> operations,
        string subscriptionId,
        string resourceGroup,
        string location,
        Dictionary<string, string> tags,
        List<string> warnings)
    {
        var name = Required(component.Name, "webApp.name");
        var planName = component.TryGetString("planName", out var pn) ? pn : $"{name}-plan";
        var skuName = component.TryGetString("sku", out var sku) ? sku : "B1";
        var runtime = component.TryGetString("runtime", out var rt) ? rt : "dotnet";
        if (component.TryGetString("network", out var network) &&
            string.Equals(network, "private", StringComparison.OrdinalIgnoreCase))
            warnings.Add($"webApp '{name}' requested private networking; v1 compiler creates secure HTTPS app service but does not yet add VNet integration/private endpoint.");
        var linuxFxVersion = RuntimeToLinuxFxVersion(runtime);
        var serverFarmId = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Web/serverfarms/{planName}";

        resources.Add(new
        {
            type = "Microsoft.Web/serverfarms",
            apiVersion = "2023-12-01",
            name = planName,
            location,
            tags,
            sku = new { name = skuName, tier = AppServiceSkuTier(skuName) },
            kind = "linux",
            properties = new { reserved = true }
        });
        resources.Add(new
        {
            type = "Microsoft.Web/sites",
            apiVersion = "2023-12-01",
            name,
            location,
            tags,
            kind = "app,linux",
            dependsOn = new[] { $"Microsoft.Web/serverfarms/{planName}" },
            properties = new
            {
                serverFarmId,
                httpsOnly = true,
                siteConfig = new { linuxFxVersion, minTlsVersion = "1.2" }
            }
        });

        AddDesired(desired, planName, "Microsoft.Web/serverfarms", resourceGroup, location, tags,
            JsonSerializer.Serialize(new { name = skuName, tier = AppServiceSkuTier(skuName) }), "linux");
        AddDesired(desired, name, "Microsoft.Web/sites", resourceGroup, location, tags, null, "app,linux");
        operations.Add(new PlanOperationDto("Create", "Microsoft.Web/serverfarms", planName, resourceGroup,
            $"Agent-generated component for webApp {name}; SKU {skuName}."));
        operations.Add(new PlanOperationDto("Create", "Microsoft.Web/sites", name, resourceGroup,
            $"Agent-generated component: webApp; runtime {runtime}; private network intent must be enforced by follow-up networking component."));
    }

    private static void AddGenericResource(
        InfraComponentSpec component,
        DesiredStateSpec desired,
        List<object> resources,
        List<PlanOperationDto> operations,
        string resourceGroup,
        string defaultLocation,
        Dictionary<string, string> inheritedTags)
    {
        var name = Required(component.Name, "genericResource.name");
        var type = component.TryGetString("type", out var t) ? t : throw new InvalidOperationException("genericResource.type is required.");
        var apiVersion = component.TryGetString("apiVersion", out var av) ? av : throw new InvalidOperationException("genericResource.apiVersion is required.");
        var location = component.TryGetString("location", out var loc) ? loc : defaultLocation;
        var kind = component.TryGetString("kindValue", out var kv) ? kv :
            component.TryGetString("kind", out var k) ? k : null;

        object? sku = null;
        string? skuJson = null;
        if (component.TryGetElementForCompiler("sku", out var skuEl))
        {
            skuJson = skuEl.GetRawText();
            sku = JsonSerializer.Deserialize<JsonElement>(skuJson);
        }

        object properties = new { };
        if (component.TryGetElementForCompiler("properties", out var propsEl))
            properties = JsonSerializer.Deserialize<JsonElement>(propsEl.GetRawText());

        var tags = new Dictionary<string, string>(inheritedTags, StringComparer.OrdinalIgnoreCase);
        if (component.TryGetElementForCompiler("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var tag in tagsEl.EnumerateObject())
                if (tag.Value.ValueKind == JsonValueKind.String)
                    tags[tag.Name] = tag.Value.GetString() ?? "";
        }

        var resource = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["apiVersion"] = apiVersion,
            ["name"] = name,
            ["location"] = location,
            ["tags"] = tags,
            ["properties"] = properties
        };
        if (sku is not null) resource["sku"] = sku;
        if (!string.IsNullOrWhiteSpace(kind)) resource["kind"] = kind;

        resources.Add(resource);
        AddDesired(desired, name, type, resourceGroup, location, tags, skuJson, kind);
        operations.Add(new PlanOperationDto("Create", type, name, resourceGroup, "Agent-generated generic ARM resource."));
    }

    private static Dictionary<string, string> ReadTags(InfraIntentSpec spec)
    {
        if (!spec.Constraints.TryGetValue("tags", out var tagsEl) || tagsEl.ValueKind != JsonValueKind.Object)
            return [];
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tagsEl.EnumerateObject())
            if (tag.Value.ValueKind == JsonValueKind.String)
                tags[tag.Name] = tag.Value.GetString() ?? "";
        return tags;
    }

    private static void AddDesired(
        DesiredStateSpec desired,
        string name,
        string type,
        string resourceGroup,
        string location,
        Dictionary<string, string> tags,
        string? skuJson = null,
        string? kind = null) =>
        desired.Nodes.Add(new DesiredResourceNode
        {
            Name = name,
            Type = type,
            ResourceGroup = resourceGroup,
            Location = location,
            Tags = new Dictionary<string, string>(tags),
            SkuJson = skuJson,
            Kind = kind
        });

    private static string RuntimeToLinuxFxVersion(string runtime) => runtime.ToLowerInvariant() switch
    {
        "dotnet" or "dotnet8" or "dotnet-8" => "DOTNETCORE|8.0",
        "node" or "node20" or "node-20" => "NODE|20-lts",
        "python" or "python312" or "python-3.12" => "PYTHON|3.12",
        _ => runtime
    };

    private static string AppServiceSkuTier(string skuName) => skuName.ToUpperInvariant() switch
    {
        "F1" => "Free",
        "D1" => "Shared",
        "B1" or "B2" or "B3" => "Basic",
        "S1" or "S2" or "S3" => "Standard",
        "P1V3" or "P2V3" or "P3V3" => "PremiumV3",
        _ => "Basic"
    };

    private static string NormalizeKind(string kind) =>
        kind.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{name} is required.") : value;

    private static string SanitizeDeploymentName(string name) =>
        new(name.Where(char.IsLetterOrDigit).Take(32).ToArray());
}
