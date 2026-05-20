using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.Library;

public sealed class LibraryScanJobService : ILibraryScanJobService
{
    private const string TaskKind = "library-scan";
    private readonly IBackgroundTaskCenter taskCenter;
    private readonly ILibraryScanner scanner;
    private readonly IScanStatusStore statusStore;
    private readonly ILibraryMetadataEnricher metadataEnricher;
    private readonly IMetadataEnrichmentStatusStore metadataStatusStore;

    public LibraryScanJobService(
        IBackgroundTaskCenter taskCenter,
        ILibraryScanner scanner,
        IScanStatusStore statusStore,
        ILibraryMetadataEnricher metadataEnricher,
        IMetadataEnrichmentStatusStore metadataStatusStore)
    {
        this.taskCenter = taskCenter;
        this.scanner = scanner;
        this.statusStore = statusStore;
        this.metadataEnricher = metadataEnricher;
        this.metadataStatusStore = metadataStatusStore;
    }

    public bool TryStartScan(LibraryRefreshRequest order, out LibraryScanStatus status)
    {
        var accepted = taskCenter.TryStartExclusive(
            TaskKind,
            "扫描并刮削媒体库",
            (_, taskProgress, cancellationToken) => RunScanAsync(null, order, taskProgress, cancellationToken),
            statusStore.MarkStarted,
            out _);
        status = statusStore.Get();
        return accepted;
    }

    public bool TryStartSourceScan(long sourceId, LibraryRefreshRequest order, out LibraryScanStatus status)
    {
        var accepted = taskCenter.TryStartExclusive(
            TaskKind,
            $"扫描并刮削媒体源 {sourceId}",
            (_, taskProgress, cancellationToken) => RunScanAsync(sourceId, order, taskProgress, cancellationToken),
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
            metadataStatusStore.MarkCancellationRequested(DateTimeOffset.UtcNow);
        }

        status = statusStore.Get();
        return canceled;
    }

    private async Task<string?> RunScanAsync(
        long? sourceId,
        LibraryRefreshRequest order,
        IProgress<BackgroundTaskProgress> taskProgress,
        CancellationToken cancellationToken)
    {
        var metadataStarted = false;
        try
        {
            var progress = new ScanTaskProgress(statusStore, taskProgress);
            var summary = sourceId.HasValue
                ? await scanner.ScanSourceAsync(sourceId.Value, progress, hideNewItemsUntilScraped: true, cancellationToken)
                : await scanner.ScanAllAsync(progress, hideNewItemsUntilScraped: true, cancellationToken);

            metadataStarted = true;
            metadataStatusStore.MarkStarted(targetLibraryItemId: null, DateTimeOffset.UtcNow);
            var metadataProgress = new MetadataTaskProgress(metadataStatusStore, taskProgress);
            var metadataSummary = await metadataEnricher.EnrichMissingAsync(metadataProgress, order, cancellationToken);
            metadataStatusStore.MarkCompleted(metadataSummary, DateTimeOffset.UtcNow);

            statusStore.MarkCompleted(summary, DateTimeOffset.UtcNow);
            return $"刷新完成：{summary.NewVideoFileCount} 个新视频，刮削 {metadataSummary.UpdatedItems} 个条目";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            statusStore.MarkCanceled(DateTimeOffset.UtcNow);
            if (metadataStarted)
            {
                metadataStatusStore.MarkCanceled(DateTimeOffset.UtcNow);
            }

            throw;
        }
        catch (Exception ex)
        {
            var message = UserFacingErrorMessages.FromException(ex);
            statusStore.MarkFailed(message, DateTimeOffset.UtcNow);
            if (metadataStarted)
            {
                metadataStatusStore.MarkFailed(message, DateTimeOffset.UtcNow);
            }

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
                "starting" => "正在启动扫描",
                "discovering" => "扫描文件",
                "probing" => "探测媒体",
                "indexing" => "写入媒体库",
                "source-completed" => "媒体源完成",
                _ => "扫描媒体库"
            };
            var count = value.Phase != "probing" && value.TotalVideoFileCount > 0
                ? $" {Math.Min(value.ProcessedVideoFileCount, value.TotalVideoFileCount)}/{value.TotalVideoFileCount}"
                : value.Phase == "discovering" && value.ProcessedVideoFileCount > 0
                    ? $" 已发现 {value.ProcessedVideoFileCount} 个视频"
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

    private sealed class MetadataTaskProgress : IProgress<LibraryMetadataEnrichmentProgress>
    {
        private readonly IMetadataEnrichmentStatusStore statusStore;
        private readonly IProgress<BackgroundTaskProgress> taskProgress;

        public MetadataTaskProgress(
            IMetadataEnrichmentStatusStore statusStore,
            IProgress<BackgroundTaskProgress> taskProgress)
        {
            this.statusStore = statusStore;
            this.taskProgress = taskProgress;
        }

        public void Report(LibraryMetadataEnrichmentProgress value)
        {
            statusStore.MarkProgress(value);
            var count = ResolveDisplayCount(value);
            taskProgress.Report(new BackgroundTaskProgress(
                value.Phase,
                FormatMetadataProgress(value),
                count is not null ? count.Value.Processed * 100d / count.Value.Target : null,
                value.CurrentTitle));
        }

        private static string FormatMetadataProgress(LibraryMetadataEnrichmentProgress value)
        {
            var phase = value.Phase switch
            {
                "starting" => "准备刮削",
                "searching" => "匹配元数据",
                "fetching-details" => "精确刷新 TMDB",
                "fetching-episodes" => "刷新分集信息和剧照",
                "downloading-poster" => "下载海报",
                "updating" => "写入元数据",
                "disabled" => "刮削已关闭",
                _ => "刮削媒体库"
            };
            var displayCount = ResolveDisplayCount(value);
            var count = displayCount is not null
                ? $" {Math.Min(displayCount.Value.Processed, displayCount.Value.Target)}/{displayCount.Value.Target}"
                : string.Empty;
            var title = string.IsNullOrWhiteSpace(value.CurrentTitle) ? string.Empty : $" · {value.CurrentTitle}";
            return $"{phase}{count}{title}";
        }

        private static (int Processed, int Target)? ResolveDisplayCount(LibraryMetadataEnrichmentProgress value)
        {
            if (value.Phase == "fetching-episodes" && value.PhaseTargetCount is > 0)
            {
                return (Math.Max(0, value.PhaseProcessedCount ?? 0), value.PhaseTargetCount.Value);
            }

            return value.TargetItemCount > 0
                ? (Math.Max(0, value.ProcessedItemCount), value.TargetItemCount)
                : null;
        }
    }
}
