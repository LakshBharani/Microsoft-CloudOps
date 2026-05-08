using Azure.Identity;
using Azure.Core;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using InfraMapper.Models;

namespace InfraMapper.Services;

public class AzureResourceService
{
    private readonly HttpClient _httpClient;
    private readonly DependencyResolver _resolver = new DependencyResolver();
    private readonly TokenCredential _credential;

    public AzureResourceService(TokenCredential credential)
    {
        _credential = credential;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
    }

    public async Task<InfrastructureGraph> GetInfrastructureAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        var json = await QueryResourceGraphAsync(
            subscriptionId,
            "Resources | project id, name, type, resourceGroup, location, tags, properties, sku, kind" +
            " | union (ResourceContainers | where type == 'microsoft.resources/subscriptions/resourcegroups'" +
            " | project id, name, type = 'Microsoft.Resources/resourceGroups', resourceGroup = name, location, tags, properties)",
            cancellationToken);

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

    public async Task<InfrastructureGraphSummary> GetInfrastructureGraphSummaryAsync(
        string subscriptionId,
        string? resourceGroupFilter = null,
        CancellationToken cancellationToken = default)
    {
        var resourceFilter = string.IsNullOrWhiteSpace(resourceGroupFilter)
            ? ""
            : $" | where resourceGroup =~ '{resourceGroupFilter.Replace("'", "''")}'";
        var rgFilter = string.IsNullOrWhiteSpace(resourceGroupFilter)
            ? ""
            : $" | where name =~ '{resourceGroupFilter.Replace("'", "''")}'";

        var query =
            "Resources | project id, name, type, resourceGroup, location, properties" + resourceFilter +
            " | union (ResourceContainers | where type == 'microsoft.resources/subscriptions/resourcegroups'" +
            rgFilter +
            " | project id, name, type = 'Microsoft.Resources/resourceGroups', resourceGroup = name, location, properties)";

        var json = await QueryResourceGraphAsync(subscriptionId, query, cancellationToken);

        var doc = JsonDocument.Parse(json);

        var nodesForResolver = new List<ResourceNode>();
        foreach (var element in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var node = new ResourceNode();
            AzureResourceGraphJson.PopulateSummaryFromRow(element, node);
            nodesForResolver.Add(node);
        }

        var edges = _resolver.ResolveDependencies(nodesForResolver);

        return new InfrastructureGraphSummary
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
    }

    private async Task<string> QueryResourceGraphAsync(
        string subscriptionId,
        string query,
        CancellationToken cancellationToken)
    {
        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(["https://management.azure.com/.default"]),
            cancellationToken);

        const string url = "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01";

        var requestBody = new
        {
            subscriptions = new[] { subscriptionId },
            query
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
