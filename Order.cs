// public class StaleOrderAutoCancelJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StaleOrderAutoCancelJob> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _staleThreshold;

    public StaleOrderAutoCancelJob(
        IServiceScopeFactory scopeFactory,
        ILogger<StaleOrderAutoCancelJob> logger,
        IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _interval = TimeSpan.FromMinutes(config.GetValue<double?>("StaleOrderJob:IntervalMinutes") ?? 15);
        _staleThreshold = TimeSpan.FromHours(config.GetValue<double?>("StaleOrderJob:ThresholdHours") ?? 24);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);

        while (!stoppingToken.IsCancellationRequested
               && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stale-order auto-cancel run failed.");
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var orderRepository = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
        var orderService = scope.ServiceProvider.GetRequiredService<IOrderService>();

        var cutoff = DateTime.UtcNow - _staleThreshold;
        var staleOrders = await orderRepository.GetStaleOrdersAsync(cutoff);

        foreach (var order in staleOrders)
        {
            try
            {
                await orderService.CancelOrderAsync(order.Id, CancelledBy.SYSTEM, requestingDoctorId: null);
                _logger.LogInformation("Auto-cancelled stale order {OrderId}.", order.Id);
            }
            catch (Exception ex)
            {
                // one bad order shouldn't kill the rest of the batch
                _logger.LogError(ex, "Failed to auto-cancel order {OrderId}.", order.Id);
            }
        }
    }
}
