using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.Library;

public sealed class LibraryMetadataEnrichmentJobService : ILibraryMetadataEnrichmentJobService
{
    private const string TaskKind = "metadata-enrichment";
    private readonly IBackgroundTaskCenter taskCenter;
    private readonly ILibraryMetadataEnricher enricher;
    private readonly IMetadataEnrichmentStatusStore statusStore;
    private readonly IAppSettingsRepository settingsRepository;
    private readonly ITmdbMetadataClient tmdbClient;
    private readonly ISubtitleCacheService? subtitleCacheService;

    public LibraryMetadataEnrichmentJobService(
        IBackgroundTaskCenter taskCenter,
        ILibraryMetadataEnricher enricher,
        IMetadataEnrichmentStatusStore statusStore,
        IAppSettingsRepository settingsRepository,
        ITmdbMetadataClient tmdbClient,
        ISubtitleCacheService? subtitleCacheService = null)
    {
        this.taskCenter = taskCenter;
        this.enricher = enricher;
        this.statusStore = statusStore;
        this.settingsRepository = settingsRepository;
        this.tmdbClient = tmdbClient;
        this.subtitleCacheService = subtitleCacheService;
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
            var settings = (await settingsRepository.GetAsync(cancellationToken)).Tmdb;
            var connectionResult = await TestTmdbConnectionAsync(settings, progress, cancellationToken);
            if ((settings.EnableMetadataEnrichment || settings.EnablePosterDownloads) && !connectionResult.IsReachable)
            {
                var message = BuildTmdbConnectionFailureMessage(connectionResult);
                statusStore.MarkFailed(message, DateTimeOffset.UtcNow);
                return message;
            }

            var summary = string.IsNullOrWhiteSpace(targetLibraryItemId)
                ? await enricher.EnrichMissingAsync(progress, cancellationToken)
                : await enricher.EnrichItemAsync(targetLibraryItemId, progress, cancellationToken);
            var subtitleSummary = subtitleCacheService is null
                ? new SubtitleCachePrewarmSummary(0, 0, 0, 0)
                : await subtitleCacheService.PrewarmLibraryAsync(
                    targetLibraryItemId,
                    taskProgress,
                    cancellationToken);
            statusStore.MarkCompleted(summary, DateTimeOffset.UtcNow);
            var subtitleText = subtitleSummary.CandidateTrackCount > 0
                ? $"，字幕缓存 {subtitleSummary.CachedTrackCount}/{subtitleSummary.CandidateTrackCount} 条"
                : string.Empty;
            return $"刮削完成：{summary.UpdatedItems} 个条目，{summary.DownloadedPosters} 张海报{subtitleText}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            statusStore.MarkCanceled(DateTimeOffset.UtcNow);
            throw;
        }
        catch (Exception ex)
        {
            statusStore.MarkFailed(UserFacingErrorMessages.FromException(ex), DateTimeOffset.UtcNow);
            throw;
        }
    }

    private async Task<TmdbConnectionTestResult> TestTmdbConnectionAsync(
        TmdbSettings settings,
        IProgress<LibraryMetadataEnrichmentProgress> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new LibraryMetadataEnrichmentProgress(
            "checking-tmdb",
            0,
            0,
            0,
            0,
            0,
            null,
            null,
            DateTimeOffset.UtcNow));

        return await tmdbClient.TestConnectionAsync(settings, cancellationToken);
    }

    private static string BuildTmdbConnectionFailureMessage(TmdbConnectionTestResult result)
    {
        var details = string.Join(
            " · ",
            new[]
            {
                string.IsNullOrWhiteSpace(result.Source) ? null : result.Source,
                result.StatusCode.HasValue ? $"HTTP {result.StatusCode.Value}" : null,
                string.IsNullOrWhiteSpace(result.Message) ? null : result.Message
            }.Where(static item => !string.IsNullOrWhiteSpace(item)));
        return string.IsNullOrWhiteSpace(details)
            ? "TMDB API 无法连接。请开启代理，或在设置中添加自定义 TMDB API 后重试刮削。"
            : $"TMDB API 无法连接。请开启代理，或在设置中添加自定义 TMDB API 后重试刮削。{details}";
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
                "checking-tmdb" => "检测 TMDB 连通性",
                "searching" => "匹配元数据",
                "fetching-details" => "精确刷新 TMDB",
                "fetching-episodes" => "刷新分集信息",
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
