using System.Text.Json;

namespace InfraMapper.Services;

public sealed class InfraIntentCompiler
{
    public static bool LooksLikeIntent(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        (root.TryGetProperty("components", out _) || root.TryGetProperty("intent", out _));
}
