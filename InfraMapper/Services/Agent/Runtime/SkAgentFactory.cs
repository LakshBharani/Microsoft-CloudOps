using Anthropic.Foundry;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;

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
        var endpoint =
            Environment.GetEnvironmentVariable("AZURE_AI_FOUNDRY_ENDPOINT")
            ?? _configuration["AzureAI:Endpoint"]
            ?? throw new InvalidOperationException(
                "Foundry endpoint not configured. Set AZURE_AI_FOUNDRY_ENDPOINT to the Claude deployment Target URI, e.g. https://<resource>.services.ai.azure.com/anthropic/v1/messages.");

        var apiKey = Environment.GetEnvironmentVariable("AZURE_AI_FOUNDRY_API_KEY")
            ?? _configuration["AzureAI:ApiKey"]
            ?? throw new InvalidOperationException(
                "Foundry API key not configured. Set AzureAI:ApiKey or AZURE_AI_FOUNDRY_API_KEY for the Claude Foundry endpoint.");

        var modelId = AgentRegistry.GetModel(agentName);
        var resourceName = ExtractResourceName(endpoint);
        var maxTokens = int.TryParse(Environment.GetEnvironmentVariable("AZURE_AI_MAX_TOKENS"), out var value) && value > 0
            ? value
            : 1600;
        var anthropicClient = new AnthropicFoundryClient(new AnthropicFoundryApiKeyCredentials(apiKey, resourceName));
        var chatClient = anthropicClient.AsIChatClient(modelId, maxTokens);

        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.Services.AddSingleton<IChatClient>(chatClient);
#pragma warning disable SKEXP0001
        kernelBuilder.Services.AddSingleton<IChatCompletionService>(sp => chatClient.AsChatCompletionService(sp));
#pragma warning restore SKEXP0001

        return kernelBuilder.Build();
    }

    private static string ExtractResourceName(string endpoint)
    {
        var host = new Uri(endpoint.Trim()).Host;
        var dot = host.IndexOf('.');
        return dot > 0 ? host[..dot] : host;
    }
}
