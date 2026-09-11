namespace WhatsAppBridge.API.Services;

/// <summary>
/// Runs <see cref="ServerMonitorService.SweepAsync"/> once a minute. The sweep exists for the
/// two alerts no incoming report can ever trigger: a reporter that died mid-outage, and a
/// reporter that went silent altogether. Both are exactly the situations where waiting for the
/// next report means waiting forever.
///
/// One minute is deliberate: the tightest threshold is five minutes, so a minute of sweep
/// granularity delays an alert by at most a fifth of the window it measures. Errors are logged
/// and the loop continues — a monitoring loop that dies on its first database hiccup is the
/// silent-watchdog problem this feature exists to solve.
/// </summary>
public sealed class MonitorSweepWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MonitorSweepWorker> _logger;

    public MonitorSweepWorker(IServiceScopeFactory scopeFactory, ILogger<MonitorSweepWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var monitor = scope.ServiceProvider.GetRequiredService<ServerMonitorService>();
                var dispatcher = scope.ServiceProvider.GetRequiredService<MonitorAlertDispatcher>();

                var alerts = await monitor.SweepAsync(DateTime.UtcNow);
                foreach (var alert in alerts)
                    await dispatcher.DispatchAsync(alert);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Monitor sweep failed; continuing on next tick.");
            }
        }
    }
}
