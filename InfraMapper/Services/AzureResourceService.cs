using Azure.Identity;
using Azure.Core;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using InfraMapper.Models;

namespace InfraMapper.Services;

public class AzureResourceService
{
    private readonly HttpClient _httpClient = new HttpClient();
    private readonly DependencyResolver _resolver = new DependencyResolver();
    private readonly TokenCredential _credential;

    public AzureResourceService(TokenCredential credential)
    {
        _credential = credential;
    }

    public async Task<InfrastructureGraph> GetInfrastructureAsync(string subscriptionId)
    {
        var token = await _credential.GetTokenAsync(
            new Azure.Core.TokenRequestContext(
                ["https://management.azure.com/.default"]
            ),
            CancellationToken.None
        );

        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.Token);

        var url = "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01";

        var requestBody = new
        {
            subscriptions = new[] { subscriptionId },
            query = "Resources | project id, name, type, resourceGroup, location, tags, properties, sku, kind" +
                    " | union (ResourceContainers | where type == 'microsoft.resources/subscriptions/resourcegroups'" +
                    " | project id, name, type = 'Microsoft.Resources/resourceGroups', resourceGroup = name, location, tags, properties)"
        };

        var content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json"
        );

        var response = await _httpClient.PostAsync(url, content);
        var json = await response.Content.ReadAsStringAsync();

        var doc = JsonDocument.Parse(json);

        var graph = new InfrastructureGraph();

        foreach (var element in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var node = new ResourceNode();
            AzureResourceGraphJson.PopulateFromRow(element, node);
            graph.Nodes.Add(node);
        }

        graph.Edges = _resolver.ResolveDependencies(graph.Nodes);

        return graph;
    }

    /// <summary>Resource Graph query without properties/tags/sku/kind — 
    /// small payload; edges still computed from structural rules.</summary>
    public async Task<InfrastructureGraphSummary> GetInfrastructureGraphSummaryAsync(string subscriptionId)
    {
        var token = await _credential.GetTokenAsync(
            new Azure.Core.TokenRequestContext(
                ["https://management.azure.com/.default"]
            ),
            CancellationToken.None
        );

        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.Token);

        var url = "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01";

        var requestBody = new
        {
            subscriptions = new[] { subscriptionId },
            // Include properties so dependency resolver can extract referenced ARM ids (workspaceId, subnetId, serverFarmId, etc.).
            // Tags/sku/kind are omitted to keep payload smaller than full inventory.
            query = "Resources | project id, name, type, resourceGroup, location, properties" +
                    " | union (ResourceContainers | where type == 'microsoft.resources/subscriptions/resourcegroups'" +
                    " | project id, name, type = 'Microsoft.Resources/resourceGroups', resourceGroup = name, location, properties)"
        };

        var content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json"
        );

        var response = await _httpClient.PostAsync(url, content);
        var json = await response.Content.ReadAsStringAsync();

        var doc = JsonDocument.Parse(json);

        var nodesForResolver = new List<ResourceNode>();
        foreach (var element in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var node = new ResourceNode();
            AzureResourceGraphJson.PopulateSummaryFromRow(element, node);
            nodesForResolver.Add(node);
        }

        var edges = _resolver.ResolveDependencies(nodesForResolver);

        var summary = new InfrastructureGraphSummary
        {
            Nodes = nodesForResolver
                .Select(n => new GraphNodeSummary
                {
                    Id = n.Id,
                    Name = n.Name,
                    Type = n.Type,
                    Location = n.Location,
                    ResourceGroup = n.ResourceGroup
                })
                .ToList(),
            Edges = edges
        };

        return summary;
    }
}