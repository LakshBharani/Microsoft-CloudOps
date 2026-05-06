using InfraMapper.Models;

namespace InfraMapper.Services;

public sealed class DiffService
{
    private readonly AzureResourceService _resourceService;

    public DiffService(AzureResourceService resourceService) =>
        _resourceService = resourceService;

    public async Task<DiffResult> ComputeAsync(string subscriptionId, DesiredStateSpec desired, CancellationToken ct)
    {
        var live = await _resourceService.GetInfrastructureAsync(subscriptionId);

        // Scope: only consider live resources in the same RGs as the desired spec
        var scopedRgs = desired.Scope.Count > 0
            ? desired.Scope.Select(r => r.ToLowerInvariant()).ToHashSet()
            : desired.Nodes.Select(n => n.ResourceGroup.ToLowerInvariant()).ToHashSet();

        var liveInScope = live.Nodes
            .Where(n => scopedRgs.Contains(n.ResourceGroup.ToLowerInvariant()))
            .ToList();

        var result = new DiffResult();

        // Index live resources by (name, type, rg) — case-insensitive
        var liveIndex = liveInScope.ToDictionary(
            n => Key(n.Name, n.Type, n.ResourceGroup),
            n => n);

        var matchedLiveKeys = new HashSet<string>();

        foreach (var desired_node in desired.Nodes)
        {
            var key = Key(desired_node.Name, desired_node.Type, desired_node.ResourceGroup);
            if (liveIndex.TryGetValue(key, out var live_node))
            {
                matchedLiveKeys.Add(key);
                var changes = DetectChanges(desired_node, live_node);
                var diff = ToDiffNode(desired_node, live_node.Id);
                diff.Changes = changes;
                if (changes.Count > 0)
                    result.ToUpdate.Add(diff);
                else
                    result.Unchanged.Add(diff);
            }
            else
            {
                result.ToCreate.Add(ToDiffNode(desired_node, null));
            }
        }

        // Live resources in scope that aren't in desired = to delete
        foreach (var live_node in liveInScope)
        {
            if (IsScopedResourceGroup(live_node, scopedRgs))
                continue;

            var key = Key(live_node.Name, live_node.Type, live_node.ResourceGroup);
            if (!matchedLiveKeys.Contains(key))
            {
                result.ToDelete.Add(new DiffNode
                {
                    Name = live_node.Name,
                    Type = live_node.Type,
                    ResourceGroup = live_node.ResourceGroup,
                    Location = live_node.Location,
                    ExistingId = live_node.Id,
                });
            }
        }

        return result;
    }

    public DiffResult ComputeDesiredStateDiff(DesiredStateSpec oldDesired, DesiredStateSpec newDesired)
    {
        var result = new DiffResult();
        var oldIndex = oldDesired.Nodes.ToDictionary(
            n => Key(n.Name, n.Type, n.ResourceGroup),
            n => n);
        var matchedOldKeys = new HashSet<string>();

        foreach (var newNode in newDesired.Nodes)
        {
            var key = Key(newNode.Name, newNode.Type, newNode.ResourceGroup);
            if (oldIndex.TryGetValue(key, out var oldNode))
            {
                matchedOldKeys.Add(key);
                var changes = DetectChanges(newNode, ToResourceNode(oldNode));
                var diff = ToDiffNode(newNode, null);
                diff.Changes = changes;
                if (changes.Count > 0)
                    result.ToUpdate.Add(diff);
                else
                    result.Unchanged.Add(diff);
            }
            else
            {
                result.ToCreate.Add(ToDiffNode(newNode, null));
            }
        }

        foreach (var oldNode in oldDesired.Nodes)
        {
            var key = Key(oldNode.Name, oldNode.Type, oldNode.ResourceGroup);
            if (!matchedOldKeys.Contains(key))
                result.ToDelete.Add(ToDiffNode(oldNode, null));
        }

        return result;
    }

    private static List<DiffChange> DetectChanges(DesiredResourceNode desired, ResourceNode live)
    {
        var changes = new List<DiffChange>();

        if (!string.IsNullOrEmpty(desired.Location) &&
            !string.Equals(desired.Location, live.Location, StringComparison.OrdinalIgnoreCase))
            changes.Add(new DiffChange { Field = "location", From = live.Location, To = desired.Location });

        if (desired.SkuJson is not null &&
            !string.Equals(desired.SkuJson, live.SkuJson, StringComparison.OrdinalIgnoreCase))
            changes.Add(new DiffChange { Field = "sku", From = live.SkuJson, To = desired.SkuJson });

        if (desired.Kind is not null &&
            !string.Equals(desired.Kind, live.Kind, StringComparison.OrdinalIgnoreCase))
            changes.Add(new DiffChange { Field = "kind", From = live.Kind, To = desired.Kind });

        foreach (var (k, v) in desired.Tags)
        {
            if (!live.Tags.TryGetValue(k, out var liveVal) || liveVal != v)
                changes.Add(new DiffChange { Field = $"tag:{k}", From = liveVal, To = v });
        }

        return changes;
    }

    private static DiffNode ToDiffNode(DesiredResourceNode n, string? existingId) => new()
    {
        Name = n.Name,
        Type = n.Type,
        ResourceGroup = n.ResourceGroup,
        Location = n.Location,
        ExistingId = existingId,
    };

    private static string Key(string name, string type, string rg) =>
        $"{name.ToLowerInvariant()}|{type.ToLowerInvariant()}|{rg.ToLowerInvariant()}";

    private static bool IsScopedResourceGroup(ResourceNode node, HashSet<string> scopedRgs) =>
        string.Equals(node.Type, "Microsoft.Resources/resourceGroups", StringComparison.OrdinalIgnoreCase) &&
        scopedRgs.Contains(node.Name.ToLowerInvariant());

    private static ResourceNode ToResourceNode(DesiredResourceNode desired) => new()
    {
        Name = desired.Name,
        Type = desired.Type,
        ResourceGroup = desired.ResourceGroup,
        Location = desired.Location,
        Tags = new Dictionary<string, string>(desired.Tags),
        SkuJson = desired.SkuJson,
        Kind = desired.Kind
    };
}
