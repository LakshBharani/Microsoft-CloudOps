using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;

namespace InfraMapper.Services.Agent.Runtime;

public sealed class SkAgentFactory
{
    private readonly Kernel _baseKernel;
    private readonly ILoggerFactory _loggerFactory;

    public SkAgentFactory(Kernel baseKernel, ILoggerFactory loggerFactory)
    {
        _baseKernel = baseKernel;
        _loggerFactory = loggerFactory;
    }

    public ChatCompletionAgent Create(
        string name,
        string instructions,
        params (object Plugin, string Name)[] plugins)
    {
        var kernel = _baseKernel.Clone();
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
}
