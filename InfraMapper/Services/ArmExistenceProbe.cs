using System.Net.Http.Headers;
using Azure.Core;

namespace InfraMapper.Services;

public sealed record ArmProbeResult(
    bool Exists,
    int? HttpStatus,
    string? ResourceJson,
    string? ErrorCode,
    string? ErrorMessage);

public sealed class ArmExistenceProbe
{
    private static readonly Dictionary<string, string> ApiVersionByType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.Resources/resourceGroups"] = "2021-04-01",
        ["Microsoft.Storage/storageAccounts"] = "2023-01-01",
        ["Microsoft.Network/virtualNetworks"] = "2023-09-01",
        ["Microsoft.Network/virtualNetworks/subnets"] = "2023-09-01",
        ["Microsoft.Network/networkSecurityGroups"] = "2023-09-01",
        ["Microsoft.Network/publicIPAddresses"] = "2023-09-01",
        ["Microsoft.KeyVault/vaults"] = "2023-07-01",
        ["Microsoft.Web/sites"] = "2022-03-01",
        ["Microsoft.Web/serverfarms"] = "2022-03-01",
        ["Microsoft.Compute/virtualMachines"] = "2023-09-01",
        ["Microsoft.Compute/virtualMachineScaleSets"] = "2023-09-01",
        ["Microsoft.Sql/servers"] = "2023-05-01-preview",
        ["Microsoft.Sql/servers/databases"] = "2023-05-01-preview",
        ["Microsoft.ContainerService/managedClusters"] = "2024-02-01",
        ["Microsoft.DocumentDB/databaseAccounts"] = "2023-04-15",
        ["Microsoft.Cache/Redis"] = "2023-08-01"
    };

    private const string DefaultApiVersion = "2022-09-01";

    private readonly HttpClient _httpClient;
    private readonly TokenCredential _credential;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private AccessToken? _cachedToken;

    public ArmExistenceProbe(TokenCredential credential)
    {
        _credential = credential;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<ArmProbeResult> ProbeAsync(string armId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(armId))
            return new ArmProbeResult(false, 400, null, "validation", "armId is required");

        var apiVersion = ResolveApiVersion(armId);
        var url = $"https://management.azure.com{(armId.StartsWith('/') ? armId : "/" + armId)}?api-version={apiVersion}";

        AccessToken token;
        try { token = await GetTokenAsync(ct); }
        catch (Exception ex)
        {
            return new ArmProbeResult(false, 401, null, "TokenAcquisitionFailed", ex.Message);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            var status = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
                return new ArmProbeResult(true, status, body, null, null);

            if (status == 404)
                return new ArmProbeResult(false, 404, null, "NotFound", null);

            return new ArmProbeResult(false, status, null, response.StatusCode.ToString(), body);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ArmProbeResult(false, 408, null, "Timeout", "ARM probe exceeded 15s.");
        }
    }

    private async Task<AccessToken> GetTokenAsync(CancellationToken ct)
    {
        var snapshot = _cachedToken;
        if (snapshot is { } valid && valid.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(2))
            return valid;

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_cachedToken is { } recheck && recheck.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(2))
                return recheck;

            using var fetchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            fetchCts.CancelAfter(TimeSpan.FromSeconds(15));
            var token = await _credential.GetTokenAsync(
                new TokenRequestContext(["https://management.azure.com/.default"]),
                fetchCts.Token);
            _cachedToken = token;
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static string ResolveApiVersion(string armId)
    {
        // armId: /subscriptions/{sub}/resourceGroups/{rg}/providers/{type}/{name}[/...]
        // Or RG: /subscriptions/{sub}/resourceGroups/{rg}
        if (!armId.Contains("/providers/", StringComparison.OrdinalIgnoreCase))
            return ApiVersionByType["Microsoft.Resources/resourceGroups"];

        var providersIndex = armId.IndexOf("/providers/", StringComparison.OrdinalIgnoreCase) + "/providers/".Length;
        var tail = armId[providersIndex..];
        var parts = tail.Split('/');
        // parts: [namespace, type, name, (subtype, name)*]
        if (parts.Length < 3) return DefaultApiVersion;

        // Compose type as namespace/type[/subtype]*
        var typeParts = new List<string> { parts[0], parts[1] };
        for (var i = 3; i + 1 < parts.Length; i += 2)
            typeParts.Add(parts[i]);
        var fullType = string.Join("/", typeParts);

        return ApiVersionByType.TryGetValue(fullType, out var apiVersion) ? apiVersion : DefaultApiVersion;
    }
}
