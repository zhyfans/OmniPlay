using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OmniPlay.Infrastructure.Maintenance;

namespace OmniPlay.Api;

public sealed class CacheMaintenanceHostedService : BackgroundService
{
    private readonly CacheMaintenanceCoordinator coordinator;
    private readonly ILogger<CacheMaintenanceHostedService> logger;
    private readonly TimeSpan initialDelay;
    private readonly TimeSpan interval;

    public CacheMaintenanceHostedService(
        CacheMaintenanceCoordinator coordinator,
        ILogger<CacheMaintenanceHostedService> logger)
    {
        this.coordinator = coordinator;
        this.logger = logger;
        initialDelay = ReadMinutes("OMNIPLAY_CACHE_MAINTENANCE_INITIAL_DELAY_MINUTES", 10);
        interval = ReadHours("OMNIPLAY_CACHE_MAINTENANCE_INTERVAL_HOURS", 6);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (initialDelay > TimeSpan.Zero)
        {
            logger.LogInformation("Cache maintenance will start after {Delay}.", initialDelay);
            await Task.Delay(initialDelay, stoppingToken);
        }

        using var timer = new PeriodicTimer(interval);
        do
        {
            await RunCycleAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        try
        {
            await coordinator.RunOnceAsync(stoppingToken);
            logger.LogInformation("Cache maintenance cycle completed.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cache maintenance cycle failed.");
        }
    }

    private static TimeSpan ReadMinutes(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) && parsed >= 0
            ? TimeSpan.FromMinutes(parsed)
            : TimeSpan.FromMinutes(defaultValue);
    }

    private static TimeSpan ReadHours(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) && parsed > 0
            ? TimeSpan.FromHours(parsed)
            : TimeSpan.FromHours(defaultValue);
    }
}
