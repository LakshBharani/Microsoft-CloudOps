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
        var branch = Uri.EscapeDataString(NormalizeBranch(cfg.Branch));
        var url = $"{ApiBase(cfg)}/items?path={path}&$format=text&versionDescriptor.version={branch}&versionDescriptor.versionType=branch&api-version=7.0";
        var res = await http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode && string.Equals(NormalizeBranch(cfg.Branch), "main", StringComparison.OrdinalIgnoreCase))
        {
            url = $"{ApiBase(cfg)}/items?path={path}&$format=text&api-version=7.0";
            res = await http.GetAsync(url, ct);
        }
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Azure DevOps item fetch failed ({(int)res.StatusCode} {res.ReasonPhrase}): {body}");
        }
        return await res.Content.ReadAsStringAsync(ct);
    }

    private static string NormalizeBranch(string branch) =>
        branch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
            ? branch["refs/heads/".Length..]
            : branch;

    public async Task PushDesiredStateAsync(AzureDevOpsConfig cfg, string content, string commitMessage, CancellationToken ct)
    {
        using var http = CreateClient(cfg);
        var branch = NormalizeBranch(cfg.Branch);

        // Get current branch ref
        var refsUrl = $"{ApiBase(cfg)}/refs?filter={Uri.EscapeDataString($"heads/{branch}")}&api-version=7.0";
        var refsRes = await http.GetAsync(refsUrl, ct);
        await EnsureSuccessWithBodyAsync(refsRes, "Azure DevOps refs fetch", ct);
        var refsJson = JsonNode.Parse(await refsRes.Content.ReadAsStringAsync(ct))!;
        var refsArray = refsJson["value"]?.AsArray();
        var oldObjectId = refsArray?.Count > 0 ? refsArray[0]?["objectId"]?.GetValue<string>() : null;

        // Branch doesn't exist — branch off main
        if (oldObjectId == null && !string.Equals(branch, "main", StringComparison.OrdinalIgnoreCase))
        {
            var mainRefsUrl = $"{ApiBase(cfg)}/refs?filter=heads/main&api-version=7.0";
            var mainRes = await http.GetAsync(mainRefsUrl, ct);
            await EnsureSuccessWithBodyAsync(mainRes, "Azure DevOps main refs fetch", ct);
            var mainJson = JsonNode.Parse(await mainRes.Content.ReadAsStringAsync(ct))!;
            var mainArray = mainJson["value"]?.AsArray();
            oldObjectId = mainArray?.Count > 0 ? mainArray[0]?["objectId"]?.GetValue<string>() : null;
            oldObjectId ??= "0000000000000000000000000000000000000000";
        }
        else if (oldObjectId == null)
        {
            throw new HttpRequestException($"Azure DevOps branch '{branch}' was not found.");
        }
        oldObjectId ??= "0000000000000000000000000000000000000000";

        // Check if file exists on the target branch (determines add vs edit)
        var existing = await GetDesiredStateAsync(cfg, ct);
        var changeType = existing == null ? "add" : "edit";
        var normalizedPath = NormalizePath(cfg.FilePath);

        var pushUrl = $"{ApiBase(cfg)}/pushes?api-version=7.0";
        var res = await PushFileChangeAsync(http, pushUrl, branch, oldObjectId, commitMessage, changeType, normalizedPath, content, ct);
        if (!res.IsSuccessStatusCode && changeType == "add")
        {
            var errorBody = await res.Content.ReadAsStringAsync(ct);
            if (errorBody.Contains("specified in the add operation already exists", StringComparison.OrdinalIgnoreCase))
            {
                oldObjectId = await GetBranchObjectIdAsync(http, cfg, branch, ct) ?? oldObjectId;
                res = await PushFileChangeAsync(http, pushUrl, branch, oldObjectId, commitMessage, "edit", normalizedPath, content, ct);
            }
            else
            {
                throw new HttpRequestException($"Azure DevOps push failed ({(int)res.StatusCode} {res.ReasonPhrase}): {errorBody}");
            }
        }
        await EnsureSuccessWithBodyAsync(res, "Azure DevOps push", ct);
    }

    private async Task<string?> GetBranchObjectIdAsync(HttpClient http, AzureDevOpsConfig cfg, string branch, CancellationToken ct)
    {
        var refsUrl = $"{ApiBase(cfg)}/refs?filter={Uri.EscapeDataString($"heads/{branch}")}&api-version=7.0";
        var refsRes = await http.GetAsync(refsUrl, ct);
        await EnsureSuccessWithBodyAsync(refsRes, "Azure DevOps refs fetch", ct);
        var refsJson = JsonNode.Parse(await refsRes.Content.ReadAsStringAsync(ct))!;
        var refsArray = refsJson["value"]?.AsArray();
        return refsArray?.Count > 0 ? refsArray[0]?["objectId"]?.GetValue<string>() : null;
    }

    private static Task<HttpResponseMessage> PushFileChangeAsync(
        HttpClient http,
        string pushUrl,
        string branch,
        string oldObjectId,
        string commitMessage,
        string changeType,
        string normalizedPath,
        string content,
        CancellationToken ct)
    {
        var push = new
        {
            refUpdates = new[] { new { name = $"refs/heads/{branch}", oldObjectId } },
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

        var body = new StringContent(JsonSerializer.Serialize(push), Encoding.UTF8, "application/json");
        return http.PostAsync(pushUrl, body, ct);
    }

    private static async Task EnsureSuccessWithBodyAsync(HttpResponseMessage res, string operation, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var body = await res.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"{operation} failed ({(int)res.StatusCode} {res.ReasonPhrase}): {body}");
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
