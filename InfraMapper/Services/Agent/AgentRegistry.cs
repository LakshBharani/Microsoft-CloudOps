namespace InfraMapper.Services.Agent;

public static class AgentRegistry
{
    private const string DefaultSonnetModel = "claude-sonnet-4-6";
    private const string DefaultHaikuModel = "claude-haiku-4-5";
    private static readonly Dictionary<string, string> ConfiguredModels = new(StringComparer.OrdinalIgnoreCase);
    private static string? _configuredDefaultModel;

    public static string GetModel(string agentName)
    {
        if (ConfiguredModels.TryGetValue(agentName, out var model) && !string.IsNullOrWhiteSpace(model))
            return model;

        if (!string.IsNullOrWhiteSpace(_configuredDefaultModel))
            return _configuredDefaultModel;

        return agentName.Equals("questioner", StringComparison.OrdinalIgnoreCase) ||
               agentName.Equals("reflector", StringComparison.OrdinalIgnoreCase)
            ? DefaultHaikuModel
            : DefaultSonnetModel;
    }

    public static void Configure(IConfiguration configuration)
    {
        ConfiguredModels.Clear();

        _configuredDefaultModel = configuration["AzureAI:DefaultDeploymentName"]
            ?? configuration["AzureAI:DeploymentName"]
            ?? configuration["AzureAI:ModelId"]
            ?? Environment.GetEnvironmentVariable("AZURE_AI_DEFAULT_DEPLOYMENT");

        var sonnet = configuration["AzureAI:SonnetDeploymentName"]
            ?? Environment.GetEnvironmentVariable("AZURE_AI_CLAUDE_SONNET_DEPLOYMENT")
            ?? _configuredDefaultModel
            ?? DefaultSonnetModel;
        var haiku = configuration["AzureAI:HaikuDeploymentName"]
            ?? Environment.GetEnvironmentVariable("AZURE_AI_CLAUDE_HAIKU_DEPLOYMENT")
            ?? DefaultHaikuModel;

        foreach (var agent in new[] { "orchestrator", "investigator", "planner", "critic", "executor" })
            ConfiguredModels[agent] = configuration[$"AzureAI:AgentModels:{agent}"] ?? sonnet;

        foreach (var agent in new[] { "questioner", "reflector" })
            ConfiguredModels[agent] = configuration[$"AzureAI:AgentModels:{agent}"] ?? haiku;
    }
}
