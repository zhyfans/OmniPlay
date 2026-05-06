using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.Library;

public sealed class LibraryScanJobService : ILibraryScanJobService
{
    private const string TaskKind = "library-scan";
    private readonly IBackgroundTaskCenter taskCenter;
    private readonly ILibraryScanner scanner;
    private readonly IScanStatusStore statusStore;

    public LibraryScanJobService(
        IBackgroundTaskCenter taskCenter,
        ILibraryScanner scanner,
        IScanStatusStore statusStore)
    {
        this.taskCenter = taskCenter;
        this.scanner = scanner;
        this.statusStore = statusStore;
    }

    public bool TryStartScan(out LibraryScanStatus status)
    {
        var accepted = taskCenter.TryStartExclusive(
            TaskKind,
            "扫描媒体库",
            (_, taskProgress, cancellationToken) => RunScanAsync(null, taskProgress, cancellationToken),
            statusStore.MarkStarted,
            out _);
        status = statusStore.Get();
        return accepted;
    }

    public bool TryStartSourceScan(long sourceId, out LibraryScanStatus status)
    {
        var accepted = taskCenter.TryStartExclusive(
            TaskKind,
            $"扫描媒体源 {sourceId}",
            (_, taskProgress, cancellationToken) => RunScanAsync(sourceId, taskProgress, cancellationToken),
            statusStore.MarkStarted,
            out _);
        status = statusStore.Get();
        return accepted;
    }

    public bool RequestCancel(out LibraryScanStatus status)
    {
        var canceled = taskCenter.TryCancelKind(TaskKind, out _);
        if (canceled)
        {
            statusStore.MarkCancellationRequested(DateTimeOffset.UtcNow);
        }

        status = statusStore.Get();
        return canceled;
    }

    private async Task<string?> RunScanAsync(
        long? sourceId,
        IProgress<BackgroundTaskProgress> taskProgress,
        CancellationToken cancellationToken)
    {
        try
        {
            var progress = new ScanTaskProgress(statusStore, taskProgress);
            var summary = sourceId.HasValue
                ? await scanner.ScanSourceAsync(sourceId.Value, progress, cancellationToken)
                : await scanner.ScanAllAsync(progress, cancellationToken);
            statusStore.MarkCompleted(summary, DateTimeOffset.UtcNow);
            return $"扫描完成：{summary.NewVideoFileCount} 个新视频";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            statusStore.MarkCanceled(DateTimeOffset.UtcNow);
            throw;
        }
        catch (Exception ex)
        {
            statusStore.MarkFailed(ex.Message, DateTimeOffset.UtcNow);
            throw;
        }
    }

    private sealed class ScanTaskProgress : IProgress<LibraryScanProgress>
    {
        private readonly IScanStatusStore statusStore;
        private readonly IProgress<BackgroundTaskProgress> taskProgress;

        public ScanTaskProgress(
            IScanStatusStore statusStore,
            IProgress<BackgroundTaskProgress> taskProgress)
        {
            this.statusStore = statusStore;
            this.taskProgress = taskProgress;
        }

        public void Report(LibraryScanProgress value)
        {
            statusStore.MarkProgress(value);
            taskProgress.Report(new BackgroundTaskProgress(
                value.Phase,
                FormatScanProgress(value),
                CalculateScanPercent(value),
                value.CurrentRelativePath));
        }

        private static string FormatScanProgress(LibraryScanProgress value)
        {
            var phase = value.Phase switch
            {
                "starting" => "准备扫描",
                "probing" => "探测媒体",
                "indexing" => "写入媒体库",
                "source-completed" => "媒体源完成",
                _ => "扫描媒体库"
            };
            var count = value.TotalVideoFileCount > 0
                ? $" {Math.Min(value.ProcessedVideoFileCount, value.TotalVideoFileCount)}/{value.TotalVideoFileCount}"
                : string.Empty;
            var probe = value.Phase == "probing" && value.ProbeCandidateCount > 0
                ? $"，探测 {value.ProbedVideoFileCount}/{value.ProbeCandidateCount}"
                : string.Empty;
            var source = string.IsNullOrWhiteSpace(value.CurrentSourceName) ? string.Empty : $" · {value.CurrentSourceName}";
            return $"{phase}{count}{probe}{source}";
        }

        private static double? CalculateScanPercent(LibraryScanProgress value)
        {
            if (value.Phase == "probing" && value.ProbeCandidateCount > 0)
            {
                return value.ProbedVideoFileCount * 100d / value.ProbeCandidateCount;
            }

            return value.TotalVideoFileCount > 0
                ? value.ProcessedVideoFileCount * 100d / value.TotalVideoFileCount
                : null;
        }
    }
}
