using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.Library;

public sealed class InMemoryScanStatusStore : IScanStatusStore
{
    private readonly object gate = new();
    private LibraryScanStatus status = new(false, null, null, null, null);

    public LibraryScanStatus Get()
    {
        lock (gate)
        {
            return status;
        }
    }

    public void MarkStarted(DateTimeOffset startedAt)
    {
        lock (gate)
        {
            status = status with
            {
                IsRunning = true,
                StartedAt = startedAt,
                CompletedAt = null,
                LastError = null,
                IsCancellationRequested = false,
                CancellationRequestedAt = null,
                Progress = new LibraryScanProgress("starting", 0, 0, null, 0, 0, 0, 0, null, startedAt),
                WasCanceled = false
            };
        }
    }

    public void MarkProgress(LibraryScanProgress progress)
    {
        lock (gate)
        {
            if (!status.IsRunning)
            {
                return;
            }

            status = status with { Progress = progress };
        }
    }

    public void MarkCancellationRequested(DateTimeOffset requestedAt)
    {
        lock (gate)
        {
            if (!status.IsRunning)
            {
                return;
            }

            status = status with
            {
                IsCancellationRequested = true,
                CancellationRequestedAt = requestedAt
            };
        }
    }

    public void MarkCompleted(LibraryScanSummary summary, DateTimeOffset completedAt)
    {
        lock (gate)
        {
            status = status with
            {
                IsRunning = false,
                CompletedAt = completedAt,
                LastSummary = summary,
                LastError = null,
                IsCancellationRequested = false,
                Progress = status.Progress is null
                    ? null
                    : status.Progress with { Phase = "completed", UpdatedAt = completedAt },
                WasCanceled = false
            };
        }
    }

    public void MarkCanceled(DateTimeOffset canceledAt)
    {
        lock (gate)
        {
            status = status with
            {
                IsRunning = false,
                CompletedAt = canceledAt,
                LastError = null,
                IsCancellationRequested = false,
                Progress = status.Progress is null
                    ? null
                    : status.Progress with { Phase = "canceled", UpdatedAt = canceledAt },
                WasCanceled = true
            };
        }
    }

    public void MarkFailed(string message, DateTimeOffset failedAt)
    {
        lock (gate)
        {
            status = status with
            {
                IsRunning = false,
                CompletedAt = failedAt,
                LastError = message,
                IsCancellationRequested = false,
                Progress = status.Progress is null
                    ? null
                    : status.Progress with { Phase = "failed", UpdatedAt = failedAt },
                WasCanceled = false
            };
        }
    }
}
