using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;

namespace InfraMapper.Services.Agent.Runtime;

public sealed class SkAgentFactory
{
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;

    public SkAgentFactory(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        _configuration = configuration;
        _loggerFactory = loggerFactory;
    }

    public ChatCompletionAgent Create(
        string name,
        string instructions,
        params (object Plugin, string Name)[] plugins)
    {
        var kernel = BuildKernel(name);
        foreach (var (plugin, pluginName) in plugins)
            kernel.Plugins.AddFromObject(plugin, pluginName);

        return new ChatCompletionAgent
        {
            Name = name,
            Instructions = instructions,
            Kernel = kernel,
            Arguments = SkAgentRunner.BuildArguments(),
            LoggerFactory = _loggerFactory
        };
    }

    private Kernel BuildKernel(string agentName)
    {
        var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")
            ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? _configuration["OpenAI:ApiKey"]
            ?? throw new InvalidOperationException(
                "OpenAI API key not configured. Set AZURE_OPENAI_API_KEY, OPENAI_API_KEY, or OpenAI:ApiKey.");

        var deploymentName = AgentRegistry.GetModel(agentName);
        var azureEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
            ?? _configuration["OpenAI:Endpoint"]
            ?? _configuration["AzureOpenAI:Endpoint"];

        var kernelBuilder = Kernel.CreateBuilder();
        if (!string.IsNullOrWhiteSpace(azureEndpoint))
        {
            kernelBuilder.AddAzureOpenAIChatCompletion(
                deploymentName: deploymentName,
                endpoint: azureEndpoint,
                apiKey: apiKey,
                modelId: deploymentName);
        }
        else
        {
            kernelBuilder.AddOpenAIChatCompletion(deploymentName, apiKey);
        }

        return kernelBuilder.Build();
    }
}
