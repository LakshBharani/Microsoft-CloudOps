namespace InfraMapper.Services.Agent;

public sealed class SessionEvictionService : BackgroundService
{
    private static readonly TimeSpan EvictAfter = TimeSpan.FromHours(4);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    private readonly ConversationStore _store;

    public SessionEvictionService(ConversationStore store) => _store = store;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            _store.Evict(EvictAfter);
    }
}
