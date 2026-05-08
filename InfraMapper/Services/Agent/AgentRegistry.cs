namespace InfraMapper.Services.Agent;

public static class AgentRegistry
{
    private const string DefaultReasoningModel = "o4-mini";
    private const string DefaultExecutionModel = "gpt-4.1-mini";
    private static readonly Dictionary<string, string> ConfiguredModels = new(StringComparer.OrdinalIgnoreCase);
    private static string? _configuredDefaultModel;

    public static string GetModel(string agentName)
    {
        if (ConfiguredModels.TryGetValue(agentName, out var model) && !string.IsNullOrWhiteSpace(model))
            return model;

        if (!string.IsNullOrWhiteSpace(_configuredDefaultModel))
            return _configuredDefaultModel;

        return agentName.Equals("questioner", StringComparison.OrdinalIgnoreCase) ||
               agentName.Equals("executor", StringComparison.OrdinalIgnoreCase) ||
               agentName.Equals("reflector", StringComparison.OrdinalIgnoreCase)
            ? DefaultExecutionModel
            : DefaultReasoningModel;
    }

    public static void Configure(IConfiguration configuration)
    {
        ConfiguredModels.Clear();

        _configuredDefaultModel = configuration["OpenAI:ModelId"]
            ?? Environment.GetEnvironmentVariable("OPENAI_MODEL")
            ?? DefaultReasoningModel;

        var reasoning = _configuredDefaultModel ?? DefaultReasoningModel;
        var mini = configuration["OpenAI:MiniModelId"]
            ?? Environment.GetEnvironmentVariable("OPENAI_MINI_MODEL")
            ?? DefaultExecutionModel;

        foreach (var agent in new[] { "orchestrator", "investigator", "planner", "critic" })
            ConfiguredModels[agent] = configuration[$"OpenAI:AgentModels:{agent}"] ?? reasoning;

        foreach (var agent in new[] { "executor", "questioner", "reflector" })
            ConfiguredModels[agent] = configuration[$"OpenAI:AgentModels:{agent}"] ?? mini;
    }
}
