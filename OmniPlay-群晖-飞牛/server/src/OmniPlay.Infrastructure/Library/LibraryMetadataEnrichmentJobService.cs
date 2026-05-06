using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.Library;

public sealed class LibraryMetadataEnrichmentJobService : ILibraryMetadataEnrichmentJobService
{
    private const string TaskKind = "metadata-enrichment";
    private readonly IBackgroundTaskCenter taskCenter;
    private readonly ILibraryMetadataEnricher enricher;
    private readonly IMetadataEnrichmentStatusStore statusStore;

    public LibraryMetadataEnrichmentJobService(
        IBackgroundTaskCenter taskCenter,
        ILibraryMetadataEnricher enricher,
        IMetadataEnrichmentStatusStore statusStore)
    {
        this.taskCenter = taskCenter;
        this.enricher = enricher;
        this.statusStore = statusStore;
    }

    public bool TryStartMissing(out LibraryMetadataEnrichmentStatus status)
    {
        return TryStart(targetLibraryItemId: null, status: out status);
    }

    public bool TryStartItem(string libraryItemId, out LibraryMetadataEnrichmentStatus status)
    {
        return TryStart(string.IsNullOrWhiteSpace(libraryItemId) ? null : libraryItemId.Trim(), out status);
    }

    public bool RequestCancel(out LibraryMetadataEnrichmentStatus status)
    {
        var canceled = taskCenter.TryCancelKind(TaskKind, out _);
        if (canceled)
        {
            statusStore.MarkCancellationRequested(DateTimeOffset.UtcNow);
        }

        status = statusStore.Get();
        return canceled;
    }

    private bool TryStart(string? targetLibraryItemId, out LibraryMetadataEnrichmentStatus status)
    {
        var title = string.IsNullOrWhiteSpace(targetLibraryItemId)
            ? "刮削媒体库"
            : "重刮削条目";
        var accepted = taskCenter.TryStartExclusive(
            TaskKind,
            title,
            (taskId, taskProgress, cancellationToken) =>
                RunEnrichmentAsync(targetLibraryItemId, taskProgress, cancellationToken),
            startedAt => statusStore.MarkStarted(targetLibraryItemId, startedAt),
            out _);
        status = statusStore.Get();
        return accepted;
    }

    private async Task<string?> RunEnrichmentAsync(
        string? targetLibraryItemId,
        IProgress<BackgroundTaskProgress> taskProgress,
        CancellationToken cancellationToken)
    {
        try
        {
            var progress = new MetadataTaskProgress(statusStore, taskProgress);
            var summary = string.IsNullOrWhiteSpace(targetLibraryItemId)
                ? await enricher.EnrichMissingAsync(progress, cancellationToken)
                : await enricher.EnrichItemAsync(targetLibraryItemId, progress, cancellationToken);
            statusStore.MarkCompleted(summary, DateTimeOffset.UtcNow);
            return $"刮削完成：{summary.UpdatedItems} 个条目，{summary.DownloadedPosters} 张海报";
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
            taskProgress.Report(new BackgroundTaskProgress(
                value.Phase,
                FormatMetadataProgress(value),
                value.TargetItemCount > 0
                    ? value.ProcessedItemCount * 100d / value.TargetItemCount
                    : null,
                value.CurrentTitle));
        }

        private static string FormatMetadataProgress(LibraryMetadataEnrichmentProgress value)
        {
            var phase = value.Phase switch
            {
                "starting" => "准备刮削",
                "searching" => "匹配元数据",
                "fetching-details" => "精确刷新 TMDB",
                "fetching-episodes" => "刷新分集信息",
                "downloading-poster" => "下载海报",
                "updating" => "写入元数据",
                "disabled" => "刮削已关闭",
                _ => "刮削媒体库"
            };
            var count = value.TargetItemCount > 0
                ? $" {Math.Min(value.ProcessedItemCount, value.TargetItemCount)}/{value.TargetItemCount}"
                : string.Empty;
            var title = string.IsNullOrWhiteSpace(value.CurrentTitle) ? string.Empty : $" · {value.CurrentTitle}";
            return $"{phase}{count}{title}";
        }
    }
}
