namespace InfraMapper.Services.Agent;

public static class AgentRegistry
{
    private const string DefaultModel = "gpt-5.1";
    private static string? _configuredModel;

    public static string GetModel(string agentName = "infra_agent") =>
        !string.IsNullOrWhiteSpace(_configuredModel) ? _configuredModel : DefaultModel;

    public static void Configure(IConfiguration configuration)
    {
        _configuredModel = configuration["OpenAI:AgentModels:infra_agent"]
            ?? configuration["OpenAI:ModelId"]
            ?? Environment.GetEnvironmentVariable("OPENAI_MODEL")
            ?? DefaultModel;
    }
}
