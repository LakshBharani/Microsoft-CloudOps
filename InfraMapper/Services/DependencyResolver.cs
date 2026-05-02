using InfraMapper.Models;

namespace InfraMapper.Services;

public class DependencyResolver
{
    private static readonly Dictionary<string, string> StrongDependencyTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.Web/sites->Microsoft.Web/serverfarms"] = "hosting",
        ["Microsoft.Web/sites->Microsoft.Storage/storageAccounts"] = "data",
        ["Microsoft.Compute/virtualMachines->Microsoft.Network/networkInterfaces"] = "network",
        ["Microsoft.Network/networkInterfaces->Microsoft.Network/virtualNetworks"] = "network",
        ["*->Microsoft.Resources/resourceGroups"] = "scope",
    };

    private static readonly Dictionary<string, double> RiskWeights = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hosting"] = 0.9,
        ["data"] = 0.8,
        ["network"] = 0.95,
        ["observability"] = 0.7,
    };

    private const double DefaultRiskWeight = 0.5;

    public List<DependencyEdge> ResolveDependencies(List<ResourceNode> nodes)
    {
        var edges = new List<DependencyEdge>();

        foreach (var node in nodes)
        {
            var dependencies = ResolveNodeDependencies(node, nodes);

            foreach (var depId in dependencies)
            {
                var target = nodes.FirstOrDefault(n => string.Equals(n.Id, depId, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                    continue;
                var dependencyType = GetDependencyType(node.Type, target?.Type);
                edges.Add(new DependencyEdge
                {
                    SourceId = node.Id,
                    TargetId = depId,
                    DependencyType = dependencyType,
                    RiskWeight = GetRiskWeight(dependencyType)
                });
            }
        }

        AddDerivedObservabilityEdges(nodes, edges);

        return edges
            .DistinctBy(e => $"{e.SourceId}->{e.TargetId}->{e.DependencyType}".ToLowerInvariant())
            .ToList();
    }

    private static void AddDerivedObservabilityEdges(IReadOnlyList<ResourceNode> nodes, List<DependencyEdge> edges)
    {
        // If a diagnostic setting exists for a resource and it points at a Log Analytics workspace,
        // create an additional direct edge: parent resource -> workspace.
        // This makes the topology useful even if you later hide diagnosticSettings nodes in a UI.
        var byId = nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var diag in nodes.Where(n =>
                     n.Type.Contains("microsoft.insights/diagnosticsettings", StringComparison.OrdinalIgnoreCase)))
        {
            var parentId = TryGetParentResourceId(diag.Id);
            if (string.IsNullOrWhiteSpace(parentId) || !byId.ContainsKey(parentId))
                continue;

            if (diag.Properties == null || diag.Properties.Count == 0)
                continue;

            var json = System.Text.Json.JsonSerializer.Serialize(diag.Properties);
            var referencedIds = System.Text.RegularExpressions.Regex.Matches(
                    json,
                    @"/subscriptions/[^""\s]+",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                .Select(m => m.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var rid in referencedIds)
            {
                if (!byId.TryGetValue(rid, out var target))
                    continue;

                if (!target.Type.Contains("microsoft.operationalinsights/workspaces", StringComparison.OrdinalIgnoreCase))
                    continue;

                edges.Add(new DependencyEdge
                {
                    SourceId = parentId,
                    TargetId = target.Id,
                    DependencyType = "observability",
                    RiskWeight = GetRiskWeight("observability")
                });
            }
        }
    }

    private List<string> ResolveNodeDependencies(ResourceNode node, List<ResourceNode> allNodes)
    {
        var deps = new List<string>();

        // RULE 0: Extension/child resources depend on their parent.
        // Example:
        // /.../sites/myapp/providers/Microsoft.Insights/diagnosticSettings/foo
        // should depend on /.../sites/myapp
        var parentId = TryGetParentResourceId(node.Id);
        if (!string.IsNullOrWhiteSpace(parentId))
            deps.Add(parentId);

        // RULE 1: App Service → App Service Plan (use actual serverFarmId from properties)
        if (node.Type.Contains("Microsoft.Web/sites", StringComparison.OrdinalIgnoreCase))
        {
            string? serverFarmId = null;
            if (node.Properties != null && node.Properties.TryGetValue("serverFarmId", out var sfVal))
                serverFarmId = sfVal?.ToString();

            var plan = serverFarmId != null
                ? allNodes.FirstOrDefault(n => string.Equals(n.Id, serverFarmId, StringComparison.OrdinalIgnoreCase))
                : allNodes.FirstOrDefault(n =>
                    n.Type.Contains("Microsoft.Web/serverfarms", StringComparison.OrdinalIgnoreCase) &&
                    n.ResourceGroup.Equals(node.ResourceGroup, StringComparison.OrdinalIgnoreCase));

            if (plan != null)
                deps.Add(plan.Id);
        }

        // RULE 2: VM → NIC
        if (node.Type.Contains("Microsoft.Compute/virtualMachines", StringComparison.OrdinalIgnoreCase))
        {
            var nic = allNodes.FirstOrDefault(n =>
                n.Type.Contains("Microsoft.Network/networkInterfaces", StringComparison.OrdinalIgnoreCase) &&
                n.ResourceGroup.Equals(node.ResourceGroup, StringComparison.OrdinalIgnoreCase)
            );

            if (nic != null)
                deps.Add(nic.Id);
        }

        // RULE 3: NIC → VNet
        if (node.Type.Contains("Microsoft.Network/networkInterfaces", StringComparison.OrdinalIgnoreCase))
        {
            var vnet = allNodes.FirstOrDefault(n =>
                n.Type.Contains("Microsoft.Network/virtualNetworks", StringComparison.OrdinalIgnoreCase) &&
                n.ResourceGroup.Equals(node.ResourceGroup, StringComparison.OrdinalIgnoreCase)
            );

            if (vnet != null)
                deps.Add(vnet.Id);
        }

        // RULE 4: EVERYTHING → Resource Group
        var rg = allNodes.FirstOrDefault(n =>
            n.Type.Contains("resourcegroups", StringComparison.OrdinalIgnoreCase) &&
            n.Name.Equals(node.ResourceGroup, StringComparison.OrdinalIgnoreCase)
        );

        if (rg != null)
            deps.Add(rg.Id);

        // FALLBACK: Regex-based extraction (exclude types handled by explicit rules above)
        if (node.Properties != null && node.Properties.Count > 0)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(node.Properties);
            var extracted = ExtractResourceIds(json)
                .Where(id => !id.Contains("/serverfarms/", StringComparison.OrdinalIgnoreCase));
            deps.AddRange(extracted);
        }

        return deps.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? TryGetParentResourceId(string resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
            return null;

        const string providersSegment = "/providers/";
        var first = resourceId.IndexOf(providersSegment, StringComparison.OrdinalIgnoreCase);
        if (first < 0)
            return null;

        var second = resourceId.IndexOf(providersSegment, first + providersSegment.Length, StringComparison.OrdinalIgnoreCase);
        if (second < 0)
            return null;

        return resourceId[..second];
    }

    private List<string> ExtractResourceIds(string json)
    {
        var ids = new List<string>();

        var matches = System.Text.RegularExpressions.Regex.Matches(
            json,
            // Capture full ARM resource ids embedded anywhere in JSON.
            // Using a negated character class avoids the previous "minimal match" truncation (e.g. ending at /providers/M).
            @"/subscriptions/[^""\s]+",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            ids.Add(match.Value);
        }

        return ids;
    }

    private static string GetDependencyType(string sourceType, string? targetType)
    {
        if (!string.IsNullOrEmpty(targetType))
        {
            var exactKey = $"{sourceType}->{targetType}";
            if (StrongDependencyTypes.TryGetValue(exactKey, out var strong))
                return strong;

            var scopeKey = $"*->{targetType}";
            if (StrongDependencyTypes.TryGetValue(scopeKey, out strong))
                return strong;
        }

        return InferDependencyTypeFromResourceTypes(sourceType, targetType);
    }

    private static string InferDependencyTypeFromResourceTypes(string sourceType, string? targetType)
    {
        var haystack = $"{sourceType} {targetType}";
        if (haystack.Contains("Compute", StringComparison.OrdinalIgnoreCase)) return "compute";
        if (haystack.Contains("Network", StringComparison.OrdinalIgnoreCase)) return "network";
        if (haystack.Contains("Storage", StringComparison.OrdinalIgnoreCase)) return "data";
        if (haystack.Contains("Web", StringComparison.OrdinalIgnoreCase)) return "service";
        return "generic";
    }

    private static double GetRiskWeight(string dependencyType) =>
        RiskWeights.TryGetValue(dependencyType, out var w) ? w : DefaultRiskWeight;
}