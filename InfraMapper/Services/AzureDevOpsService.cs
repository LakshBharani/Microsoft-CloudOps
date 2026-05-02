using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InfraMapper.Services;

public sealed class AzureDevOpsConfig
{
    public string OrgUrl { get; set; } = "";       // e.g. https://dev.azure.com/myorg
    public string Project { get; set; } = "";
    public string Repository { get; set; } = "";
    public string Pat { get; set; } = "";
    public string Branch { get; set; } = "main";
    public string FilePath { get; set; } = "infra/desired-state.json";
}

public sealed class AzureDevOpsService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private HttpClient CreateClient(AzureDevOpsConfig cfg)
    {
        var http = new HttpClient();
        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{cfg.Pat}"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    private string ApiBase(AzureDevOpsConfig cfg) =>
        $"{cfg.OrgUrl.TrimEnd('/')}/{Uri.EscapeDataString(cfg.Project)}/_apis/git/repositories/{Uri.EscapeDataString(cfg.Repository)}";

    private static string NormalizePath(string filePath) =>
        filePath.StartsWith('/') ? filePath : "/" + filePath;

    public async Task<string?> GetDesiredStateAsync(AzureDevOpsConfig cfg, CancellationToken ct)
    {
        using var http = CreateClient(cfg);
        var path = Uri.EscapeDataString(NormalizePath(cfg.FilePath));
        var url = $"{ApiBase(cfg)}/items?path={path}&$format=text&api-version=7.0";
        var res = await http.GetAsync(url, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    public async Task PushDesiredStateAsync(AzureDevOpsConfig cfg, string content, string commitMessage, CancellationToken ct)
    {
        using var http = CreateClient(cfg);

        // Get current branch ref
        var refsUrl = $"{ApiBase(cfg)}/refs?filter=heads/{cfg.Branch}&api-version=7.0";
        var refsRes = await http.GetAsync(refsUrl, ct);
        refsRes.EnsureSuccessStatusCode();
        var refsJson = JsonNode.Parse(await refsRes.Content.ReadAsStringAsync(ct))!;
        var refsArray = refsJson["value"]?.AsArray();
        var oldObjectId = refsArray?.Count > 0 ? refsArray[0]?["objectId"]?.GetValue<string>() : null;

        // Branch doesn't exist — branch off main
        if (oldObjectId == null && cfg.Branch != "main")
        {
            var mainRefsUrl = $"{ApiBase(cfg)}/refs?filter=heads/main&api-version=7.0";
            var mainRes = await http.GetAsync(mainRefsUrl, ct);
            mainRes.EnsureSuccessStatusCode();
            var mainJson = JsonNode.Parse(await mainRes.Content.ReadAsStringAsync(ct))!;
            var mainArray = mainJson["value"]?.AsArray();
            oldObjectId = mainArray?.Count > 0 ? mainArray[0]?["objectId"]?.GetValue<string>() : null;
            oldObjectId ??= "0000000000000000000000000000000000000000";
        }
        oldObjectId ??= "0000000000000000000000000000000000000000";

        // Check if file exists on the target branch (determines add vs edit)
        var existing = await GetDesiredStateAsync(cfg, ct);
        var changeType = existing == null ? "add" : "edit";
        var normalizedPath = NormalizePath(cfg.FilePath);

        var push = new
        {
            refUpdates = new[] { new { name = $"refs/heads/{cfg.Branch}", oldObjectId } },
            commits = new[]
            {
                new
                {
                    comment = commitMessage,
                    changes = new[]
                    {
                        new
                        {
                            changeType,
                            item = new { path = normalizedPath },
                            newContent = new { content, contentType = "rawtext" }
                        }
                    }
                }
            }
        };

        var pushUrl = $"{ApiBase(cfg)}/pushes?api-version=7.0";
        var body = new StringContent(JsonSerializer.Serialize(push), Encoding.UTF8, "application/json");
        var res = await http.PostAsync(pushUrl, body, ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task PostPrCommentAsync(AzureDevOpsConfig cfg, int prId, string markdown, CancellationToken ct)
    {
        using var http = CreateClient(cfg);
        var url = $"{ApiBase(cfg)}/pullRequests/{prId}/threads?api-version=7.0";
        var payload = new
        {
            comments = new[] { new { parentCommentId = 0, content = markdown, commentType = 1 } },
            status = 1
        };
        var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var res = await http.PostAsync(url, body, ct);
        res.EnsureSuccessStatusCode();
    }
}
