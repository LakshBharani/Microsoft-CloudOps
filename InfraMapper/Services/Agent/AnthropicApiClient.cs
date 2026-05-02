using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InfraMapper.Services.Agent;

internal sealed class AnthropicApiClient
{
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;

    public AnthropicApiClient(HttpClient http) => _http = http;

    public async Task<MessagesResponse> SendAsync(MessagesRequest request, CancellationToken ct)
    {
        int[] retryDelays = [30, 60, 120];
        for (int attempt = 0; ; attempt++)
        {
            var response = await _http.PostAsJsonAsync("v1/messages", request, JsonOpts, ct);
            if (response.IsSuccessStatusCode)
                return (await response.Content.ReadFromJsonAsync<MessagesResponse>(JsonOpts, ct))!;

            var body = await response.Content.ReadAsStringAsync(ct);
            var status = (int)response.StatusCode;

            if (status == 429 && attempt < retryDelays.Length)
            {
                // Honour Retry-After if present, otherwise use fixed back-off
                int delaySec = retryDelays[attempt];
                if (response.Headers.TryGetValues("retry-after", out var vals) &&
                    int.TryParse(vals.FirstOrDefault(), out int suggested))
                    delaySec = Math.Max(delaySec, suggested);

                await Task.Delay(TimeSpan.FromSeconds(delaySec), ct);
                continue;
            }

            throw new InvalidOperationException($"Anthropic API error {status}: {body}");
        }
    }
}
