using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Api;

public sealed class LibraryRefreshSchedulerHostedService : BackgroundService
{
    private readonly IAppSettingsRepository settingsRepository;
    private readonly ILibraryScanJobService scanJobService;
    private readonly IBackgroundTaskCenter taskCenter;
    private readonly ILogger<LibraryRefreshSchedulerHostedService> logger;
    private readonly TimeSpan pollInterval;
    private DateTimeOffset? nextDueAt;
    private int? activeIntervalHours;
    private string? lastBusyTaskId;

    public LibraryRefreshSchedulerHostedService(
        IAppSettingsRepository settingsRepository,
        ILibraryScanJobService scanJobService,
        IBackgroundTaskCenter taskCenter,
        ILogger<LibraryRefreshSchedulerHostedService> logger)
    {
        this.settingsRepository = settingsRepository;
        this.scanJobService = scanJobService;
        this.taskCenter = taskCenter;
        this.logger = logger;
        pollInterval = ReadSeconds("OMNIPLAY_LIBRARY_REFRESH_SCHEDULER_INTERVAL_SECONDS", 60);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(pollInterval);
        do
        {
            await RunCycleAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await settingsRepository.GetAsync(cancellationToken);
            var automation = settings.Automation;
            if (!automation.ScheduledLibraryRefreshEnabled)
            {
                nextDueAt = null;
                activeIntervalHours = null;
                lastBusyTaskId = null;
                return;
            }

            var intervalHours = Math.Clamp(automation.ScheduledLibraryRefreshIntervalHours, 1, 24 * 30);
            var refreshInterval = TimeSpan.FromHours(intervalHours);
            var now = DateTimeOffset.Now;
            if (nextDueAt is null || activeIntervalHours != intervalHours)
            {
                activeIntervalHours = intervalHours;
                nextDueAt = now.Add(refreshInterval);
                lastBusyTaskId = null;
                logger.LogInformation(
                    "Scheduled library refresh will run every {IntervalHours} hours; next due at {NextDueAt}.",
                    intervalHours,
                    nextDueAt.Value);
                return;
            }

            if (now < nextDueAt.Value)
            {
                return;
            }

            var dueAt = nextDueAt.Value;
            var activeTask = taskCenter.GetSnapshot().ActiveTask;
            if (activeTask is not null)
            {
                if (string.Equals(activeTask.Kind, "library-scan", StringComparison.OrdinalIgnoreCase))
                {
                    ScheduleNext(now, refreshInterval);
                    lastBusyTaskId = null;
                    logger.LogInformation(
                        "Scheduled library refresh due at {DueAt} is already covered by running scan task {TaskId}.",
                        dueAt,
                        activeTask.Id);
                    return;
                }

                LogBusyTaskOnce(dueAt, activeTask);
                return;
            }

            if (scanJobService.TryStartScan(new LibraryRefreshRequest(), out _))
            {
                ScheduleNext(now, refreshInterval);
                lastBusyTaskId = null;
                logger.LogInformation("Scheduled library refresh started; next due at {NextDueAt}.", nextDueAt.Value);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scheduled library refresh cycle failed.");
        }
    }

    private void ScheduleNext(DateTimeOffset now, TimeSpan refreshInterval)
    {
        nextDueAt = now.Add(refreshInterval);
    }

    private void LogBusyTaskOnce(DateTimeOffset dueAt, BackgroundTaskStatus activeTask)
    {
        if (string.Equals(lastBusyTaskId, activeTask.Id, StringComparison.Ordinal))
        {
            return;
        }

        lastBusyTaskId = activeTask.Id;
        logger.LogInformation(
            "Scheduled library refresh due at {DueAt} is pending because task {TaskKind} ({TaskId}) is running.",
            dueAt,
            activeTask.Kind,
            activeTask.Id);
    }

    private static TimeSpan ReadSeconds(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) && parsed > 0
            ? TimeSpan.FromSeconds(parsed)
            : TimeSpan.FromSeconds(defaultValue);
    }
}
