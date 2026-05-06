namespace InfraMapper.Services.Agent;

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
        ["questioner"]    = "claude-haiku-4-5",
        ["executor"]      = "claude-haiku-4-5",
        ["reflector"]     = "claude-haiku-4-5",
    };

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
