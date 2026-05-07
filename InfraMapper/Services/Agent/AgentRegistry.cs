namespace InfraMapper.Services.Agent;

public static class AgentRegistry
{
    private const string DefaultModel = "gpt-4.1-mini";
    private static string? _configuredModelId;

    public static string GetModel(string agentName)
    {
        if (!string.IsNullOrWhiteSpace(_configuredModelId)) return _configuredModelId;
        return Environment.GetEnvironmentVariable("AZURE_OPENAI_MODEL_ID")
            ?? DefaultModel;
    }

    public static void Configure(IConfiguration configuration)
    {
        _configuredModelId = configuration["AzureAI:ModelId"]
            ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_MODEL_ID")
            ?? configuration["AzureAI:DeploymentName"];
    }
}
