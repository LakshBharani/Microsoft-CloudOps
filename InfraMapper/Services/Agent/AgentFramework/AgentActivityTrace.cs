namespace InfraMapper.Services.Agent.AgentFramework;

internal sealed class AgentActivityTraceScope : IDisposable
{
    private readonly Stack<List<AgentStreamEvent>> _stack;
    public List<AgentStreamEvent> Events { get; } = [];

    public AgentActivityTraceScope(Stack<List<AgentStreamEvent>> stack)
    {
        _stack = stack;
        _stack.Push(Events);
    }

    public void Dispose()
    {
        if (_stack.Count > 0 && ReferenceEquals(_stack.Peek(), Events))
            _stack.Pop();
    }
}

internal static class AgentActivityTrace
{
    private static readonly AsyncLocal<Stack<List<AgentStreamEvent>>?> Current = new();

    public static AgentActivityTraceScope Push()
    {
        Current.Value ??= new Stack<List<AgentStreamEvent>>();
        return new AgentActivityTraceScope(Current.Value);
    }

    public static void Record(AgentStreamEvent evt)
    {
        var stack = Current.Value;
        if (stack is null || stack.Count == 0) return;
        stack.Peek().Add(evt);
    }
}
