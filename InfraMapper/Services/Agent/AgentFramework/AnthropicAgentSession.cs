using System.Diagnostics.CodeAnalysis;
using Anthropic.Models.Messages;

namespace InfraMapper.Services.Agent.AgentFramework;

public sealed class AnthropicAgentSession
{
    public List<MessageParam> History { get; } = new();

    private readonly Dictionary<string, object?> _bag = new();

    public void SetValue(string key, object? value) => _bag[key] = value;

    public bool TryGetValue<T>(string key, [MaybeNullWhen(false)] out T value)
    {
        if (_bag.TryGetValue(key, out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }
        value = default;
        return false;
    }

    public void TryRemoveValue(string key) => _bag.Remove(key);
}
