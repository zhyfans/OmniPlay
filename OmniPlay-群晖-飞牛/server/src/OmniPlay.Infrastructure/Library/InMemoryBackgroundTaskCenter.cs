using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.Library;

public sealed class InMemoryBackgroundTaskCenter : IBackgroundTaskCenter
{
    private const int MaxRetainedTasks = 50;
    private readonly object gate = new();
    private readonly List<TrackedTask> tasks = [];

    public BackgroundTaskSnapshot GetSnapshot()
    {
        lock (gate)
        {
            var statuses = tasks
                .Select(static task => task.Status)
                .OrderByDescending(static status => status.CreatedAt)
                .ToArray();
            return new BackgroundTaskSnapshot(
                statuses,
                statuses.FirstOrDefault(static status => status.IsRunning));
        }
    }

    public bool TryStartExclusive(
        string kind,
        string title,
        Func<string, IProgress<BackgroundTaskProgress>, CancellationToken, Task<string?>> executeAsync,
        Action<DateTimeOffset>? onAccepted,
        out BackgroundTaskStatus status)
    {
        CancellationTokenSource cancellation = new();
        TrackedTask task;
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            var active = tasks.FirstOrDefault(static item => item.Status.IsRunning);
            if (active is not null)
            {
                cancellation.Dispose();
                status = active.Status;
                return false;
            }

            task = new TrackedTask(cancellation, new BackgroundTaskStatus(
                StableTaskId(kind),
                kind,
                title,
                "running",
                IsRunning: true,
                IsCancellationRequested: false,
                CanCancel: true,
                CreatedAt: now,
                StartedAt: now,
                CompletedAt: null,
                Phase: "starting",
                ProgressText: "准备执行",
                ProgressPercent: null,
                CurrentItem: null,
                ResultText: null,
                ErrorMessage: null));
            tasks.Insert(0, task);
            TrimCompletedTasks();
            status = task.Status;
        }

        onAccepted?.Invoke(now);
        var progress = new InlineProgress<BackgroundTaskProgress>(value => MarkProgress(task.Status.Id, value));
        _ = Task.Run(() => RunTaskAsync(task, executeAsync, progress));
        return true;
    }

    public bool TryCancel(string taskId, out BackgroundTaskStatus status)
    {
        lock (gate)
        {
            var task = tasks.FirstOrDefault(item => string.Equals(item.Status.Id, taskId, StringComparison.Ordinal));
            if (task is null || !task.Status.IsRunning)
            {
                status = task?.Status ?? EmptyMissingStatus(taskId);
                return false;
            }

            task.Status = task.Status with
            {
                IsCancellationRequested = true,
                ProgressText = "正在取消",
                CanCancel = false
            };
            task.Cancellation.Cancel();
            status = task.Status;
            return true;
        }
    }

    public bool TryCancelKind(string kind, out BackgroundTaskStatus status)
    {
        lock (gate)
        {
            var task = tasks.FirstOrDefault(item =>
                item.Status.IsRunning
                && string.Equals(item.Status.Kind, kind, StringComparison.OrdinalIgnoreCase));
            if (task is null)
            {
                status = EmptyMissingStatus(kind);
                return false;
            }

            task.Status = task.Status with
            {
                IsCancellationRequested = true,
                ProgressText = "正在取消",
                CanCancel = false
            };
            task.Cancellation.Cancel();
            status = task.Status;
            return true;
        }
    }

    private async Task RunTaskAsync(
        TrackedTask task,
        Func<string, IProgress<BackgroundTaskProgress>, CancellationToken, Task<string?>> executeAsync,
        IProgress<BackgroundTaskProgress> progress)
    {
        try
        {
            var result = await executeAsync(task.Status.Id, progress, task.Cancellation.Token);
            MarkCompleted(task.Status.Id, result);
        }
        catch (OperationCanceledException) when (task.Cancellation.IsCancellationRequested)
        {
            MarkCanceled(task.Status.Id);
        }
        catch (Exception ex)
        {
            MarkFailed(task.Status.Id, UserFacingErrorMessages.FromException(ex));
        }
        finally
        {
            task.Cancellation.Dispose();
        }
    }

    private void MarkProgress(string taskId, BackgroundTaskProgress progress)
    {
        lock (gate)
        {
            var task = tasks.FirstOrDefault(item => string.Equals(item.Status.Id, taskId, StringComparison.Ordinal));
            if (task is null || !task.Status.IsRunning)
            {
                return;
            }

            task.Status = task.Status with
            {
                Phase = progress.Phase,
                ProgressText = progress.Message,
                ProgressPercent = progress.Percent is null ? null : Math.Clamp(progress.Percent.Value, 0, 100),
                CurrentItem = progress.CurrentItem
            };
        }
    }

    private void MarkCompleted(string taskId, string? result)
    {
        lock (gate)
        {
            var task = tasks.FirstOrDefault(item => string.Equals(item.Status.Id, taskId, StringComparison.Ordinal));
            if (task is null)
            {
                return;
            }

            task.Status = task.Status with
            {
                State = "completed",
                IsRunning = false,
                IsCancellationRequested = false,
                CanCancel = false,
                CompletedAt = DateTimeOffset.UtcNow,
                Phase = "completed",
                ProgressPercent = 100,
                ResultText = result,
                ErrorMessage = null
            };
            TrimCompletedTasks();
        }
    }

    private void MarkCanceled(string taskId)
    {
        lock (gate)
        {
            var task = tasks.FirstOrDefault(item => string.Equals(item.Status.Id, taskId, StringComparison.Ordinal));
            if (task is null)
            {
                return;
            }

            task.Status = task.Status with
            {
                State = "canceled",
                IsRunning = false,
                IsCancellationRequested = false,
                CanCancel = false,
                CompletedAt = DateTimeOffset.UtcNow,
                Phase = "canceled",
                ProgressText = "已取消"
            };
            TrimCompletedTasks();
        }
    }

    private void MarkFailed(string taskId, string message)
    {
        lock (gate)
        {
            var task = tasks.FirstOrDefault(item => string.Equals(item.Status.Id, taskId, StringComparison.Ordinal));
            if (task is null)
            {
                return;
            }

            task.Status = task.Status with
            {
                State = "failed",
                IsRunning = false,
                IsCancellationRequested = false,
                CanCancel = false,
                CompletedAt = DateTimeOffset.UtcNow,
                Phase = "failed",
                ProgressText = message,
                ErrorMessage = message
            };
            TrimCompletedTasks();
        }
    }

    private void TrimCompletedTasks()
    {
        if (tasks.Count <= MaxRetainedTasks)
        {
            return;
        }

        tasks.RemoveRange(MaxRetainedTasks, tasks.Count - MaxRetainedTasks);
    }

    private static string StableTaskId(string kind)
    {
        return $"{kind}_{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";
    }

    private static BackgroundTaskStatus EmptyMissingStatus(string taskId)
    {
        var now = DateTimeOffset.UtcNow;
        return new BackgroundTaskStatus(
            taskId,
            "unknown",
            "未知任务",
            "missing",
            IsRunning: false,
            IsCancellationRequested: false,
            CanCancel: false,
            CreatedAt: now,
            StartedAt: null,
            CompletedAt: now,
            Phase: null,
            ProgressText: null,
            ProgressPercent: null,
            CurrentItem: null,
            ResultText: null,
            ErrorMessage: "任务不存在或未运行。");
    }

    private sealed class TrackedTask
    {
        public TrackedTask(CancellationTokenSource cancellation, BackgroundTaskStatus status)
        {
            Cancellation = cancellation;
            Status = status;
        }

        public CancellationTokenSource Cancellation { get; }

        public BackgroundTaskStatus Status { get; set; }
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> report;

        public InlineProgress(Action<T> report)
        {
            this.report = report;
        }

        public void Report(T value)
        {
            report(value);
        }
    }
}
