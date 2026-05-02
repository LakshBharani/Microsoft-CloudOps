namespace InfraMapper.Services.Agent;

/// <summary>
/// Resolves agent model names from environment variables.
/// Convention: AGENT_{NAME}_MODEL (upper-cased agent name).
/// Falls back to DEFAULT_MODEL if the specific var is not set.
/// </summary>
public static class AgentRegistry
{
    // Default models per tier:
    //  - Haiku for routing/reading/executing/reflecting (cheap, fast)
    //  - Sonnet for planning/critiquing (quality, higher cost)
    private static readonly Dictionary<string, string> DefaultModels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["orchestrator"] = "claude-haiku-4-5",
        ["investigator"]  = "claude-haiku-4-5",
        ["planner"]       = "claude-sonnet-4-6",
        ["critic"]        = "claude-sonnet-4-6",
        ["executor"]      = "claude-haiku-4-5",
        ["reflector"]     = "claude-haiku-4-5",
    };

    /// <summary>
    /// Returns the model name for <paramref name="agentName"/>.
    /// Reads AGENT_{NAME}_MODEL env var first; falls back to coded default; falls back to claude-haiku-4-5.
    /// </summary>
    public static string GetModel(string agentName)
    {
        var envKey = $"AGENT_{agentName.ToUpperInvariant()}_MODEL";
        var fromEnv = Environment.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

        // Also respect the legacy ANTHROPIC_MODEL var as ultimate fallback.
        if (DefaultModels.TryGetValue(agentName, out var def)) return def;
        return Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") ?? "claude-haiku-4-5";
    }
}
