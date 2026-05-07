namespace InfraMapper.Services.Agent.Runtime;

internal sealed class AgentStreamTraceScope : IDisposable
{
    private readonly Stack<List<AgentStreamEvent>> _stack;
    public List<AgentStreamEvent> Events { get; } = [];

    public AgentStreamTraceScope(Stack<List<AgentStreamEvent>> stack)
    {
        _stack = stack;
        _stack.Push(Events);
    }

    public void Dispose()
    {
        if (_stack.Count > 0)
            _stack.Pop();
    }
}

internal static class AgentStreamTrace
{
    private static readonly AsyncLocal<Stack<List<AgentStreamEvent>>?> Current = new();

    public static AgentStreamTraceScope Push()
    {
        Current.Value ??= new Stack<List<AgentStreamEvent>>();
        return new AgentStreamTraceScope(Current.Value);
    }

    public static void Record(AgentStreamEvent evt)
    {
        var stack = Current.Value;
        if (stack is { Count: > 0 })
            stack.Peek().Add(evt);
    }
}
