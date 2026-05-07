using Microsoft.SemanticKernel.Agents;
using System.Collections.Concurrent;

namespace InfraMapper.Services.Agent.Runtime;

public sealed class SkAgentSession
{
    private readonly ConcurrentDictionary<string, object> _values = new();

    public AgentThread? Thread { get; set; }

    public void SetValue<T>(string key, T value) where T : notnull =>
        _values[key] = value;

    public bool TryGetValue<T>(string key, out T? value)
    {
        if (_values.TryGetValue(key, out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    public bool TryRemoveValue(string key) =>
        _values.TryRemove(key, out _);
}
