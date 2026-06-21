using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.Library;
using Xunit;

namespace OmniPlay.Tests;

public sealed class LibraryScanJobServiceTests
{
    [Fact]
    public async Task TryStartScanRunsScanInBackgroundAndStoresSummary()
    {
        var scanner = new ControlledScanner();
        var statusStore = new InMemoryScanStatusStore();
        var metadataStatusStore = new InMemoryMetadataEnrichmentStatusStore();
        var service = new LibraryScanJobService(
            new InMemoryBackgroundTaskCenter(),
            scanner,
            statusStore,
            new FakeMetadataEnricher(),
            metadataStatusStore,
            new FakeAppSettingsRepository(),
            new AlwaysReachableTmdbClient());

        Assert.True(service.TryStartScan(new LibraryRefreshRequest(), out var started));
        Assert.True(started.IsRunning);
        Assert.False(service.TryStartScan(new LibraryRefreshRequest(), out var duplicate));
        Assert.True(duplicate.IsRunning);

        await scanner.WaitUntilStartedAsync();
        scanner.Complete(new LibraryScanSummary(1, 2, 3, 0, 1));
        var completed = await WaitForStatusAsync(statusStore, static status => !status.IsRunning);

        Assert.False(completed.IsRunning);
        Assert.False(completed.WasCanceled);
        Assert.NotNull(completed.LastSummary);
        Assert.Equal(3, completed.LastSummary.NewVideoFileCount);
    }

    [Fact]
    public async Task RequestCancelCancelsRunningScan()
    {
        var scanner = new ControlledScanner();
        var statusStore = new InMemoryScanStatusStore();
        var service = new LibraryScanJobService(
            new InMemoryBackgroundTaskCenter(),
            scanner,
            statusStore,
            new FakeMetadataEnricher(),
            new InMemoryMetadataEnrichmentStatusStore(),
            new FakeAppSettingsRepository(),
            new AlwaysReachableTmdbClient());

        Assert.True(service.TryStartScan(new LibraryRefreshRequest(), out _));
        await scanner.WaitUntilStartedAsync();
        Assert.True(service.RequestCancel(out var canceling));
        Assert.True(canceling.IsCancellationRequested || canceling.WasCanceled);

        var canceled = await WaitForStatusAsync(statusStore, static status => !status.IsRunning);

        Assert.True(canceled.WasCanceled);
        Assert.Null(canceled.LastError);
    }

    private static async Task<LibraryScanStatus> WaitForStatusAsync(
        IScanStatusStore statusStore,
        Func<LibraryScanStatus, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!timeout.IsCancellationRequested)
        {
            var status = statusStore.Get();
            if (predicate(status))
            {
                return status;
            }

            await Task.Delay(10, timeout.Token);
        }

        throw new TimeoutException("Timed out waiting for scan status.");
    }

    private sealed class ControlledScanner : ILibraryScanner
    {
        private readonly TaskCompletionSource<LibraryScanSummary> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<LibraryScanSummary> ScanAllAsync(CancellationToken cancellationToken = default)
        {
            return ScanAllAsync(null, cancellationToken);
        }

        public async Task<LibraryScanSummary> ScanAllAsync(
            IProgress<LibraryScanProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            return await ScanAllAsync(progress, hideNewItemsUntilScraped: false, cancellationToken);
        }

        public async Task<LibraryScanSummary> ScanAllAsync(
            IProgress<LibraryScanProgress>? progress,
            bool hideNewItemsUntilScraped,
            CancellationToken cancellationToken = default)
        {
            progress?.Report(new LibraryScanProgress(
                "probing",
                SourceCount: 1,
                CompletedSourceCount: 0,
                CurrentSourceName: "测试媒体",
                TotalVideoFileCount: 1,
                ProcessedVideoFileCount: 0,
                ProbeCandidateCount: 1,
                ProbedVideoFileCount: 0,
                CurrentRelativePath: "movie.mkv",
                DateTimeOffset.UtcNow));
            started.SetResult();
            return await completion.Task.WaitAsync(cancellationToken);
        }

        public Task<LibraryScanSummary> ScanSourceAsync(
            long sourceId,
            IProgress<LibraryScanProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            return ScanAllAsync(progress, cancellationToken);
        }

        public Task<LibraryScanSummary> ScanSourceAsync(
            long sourceId,
            IProgress<LibraryScanProgress>? progress,
            bool hideNewItemsUntilScraped,
            CancellationToken cancellationToken = default)
        {
            return ScanAllAsync(progress, hideNewItemsUntilScraped, cancellationToken);
        }

        public Task WaitUntilStartedAsync()
        {
            return started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }

        public void Complete(LibraryScanSummary summary)
        {
            completion.TrySetResult(summary);
        }
    }

    private sealed class FakeMetadataEnricher : ILibraryMetadataEnricher
    {
        public Task<LibraryMetadataEnrichmentSummary> EnrichMissingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LibraryMetadataEnrichmentSummary(0, 0, 0, 0));
        }

        public Task<LibraryMetadataEnrichmentSummary> EnrichMissingAsync(
            IProgress<LibraryMetadataEnrichmentProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            return EnrichMissingAsync(progress, new LibraryRefreshRequest(), cancellationToken);
        }

        public Task<LibraryMetadataEnrichmentSummary> EnrichMissingAsync(
            IProgress<LibraryMetadataEnrichmentProgress>? progress,
            LibraryRefreshRequest order,
            CancellationToken cancellationToken = default)
        {
            progress?.Report(new LibraryMetadataEnrichmentProgress(
                "starting",
                0,
                0,
                0,
                0,
                0,
                null,
                null,
                DateTimeOffset.UtcNow));
            return Task.FromResult(new LibraryMetadataEnrichmentSummary(0, 0, 0, 0));
        }

        public Task<LibraryMetadataEnrichmentSummary> EnrichItemAsync(
            string libraryItemId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LibraryMetadataEnrichmentSummary(0, 0, 0, 0));
        }

        public Task<LibraryMetadataEnrichmentSummary> EnrichItemAsync(
            string libraryItemId,
            IProgress<LibraryMetadataEnrichmentProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            return EnrichItemAsync(libraryItemId, cancellationToken);
        }
    }

    private sealed class FakeAppSettingsRepository : IAppSettingsRepository
    {
        public Task<AppSettingsSnapshot> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AppSettingsSnapshot(
                "OmniPlay",
                "test",
                new TmdbSettings(),
                new CacheSettings(),
                new PlaybackSettings(),
                new ProxySettings(),
                new AutomationSettings()));
        }

        public Task<AppSettingsSnapshot> UpdateAsync(
            AppSettingsUpdateRequest request,
            CancellationToken cancellationToken = default)
        {
            return GetAsync(cancellationToken);
        }
    }

    private sealed class AlwaysReachableTmdbClient : ITmdbMetadataClient
    {
        public Task<TmdbConnectionTestResult> TestConnectionAsync(
            TmdbSettings settings,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TmdbConnectionTestResult(true, "测试", 200, "TMDB 连接正常。"));
        }

        public Task<TmdbMetadataMatch?> SearchAsync(
            string mediaType,
            string title,
            string? year,
            TmdbSettings settings,
            string? secondaryQuery = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<TmdbMetadataMatch?>(null);
        }

        public Task<IReadOnlyList<TmdbMetadataMatch>> SearchCandidatesAsync(
            string mediaType,
            string title,
            string? year,
            TmdbSettings settings,
            string? secondaryQuery = null,
            int limit = 8,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<TmdbMetadataMatch>>([]);
        }

        public Task<TmdbMetadataMatch?> GetDetailsAsync(
            string mediaType,
            int tmdbId,
            TmdbSettings settings,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<TmdbMetadataMatch?>(null);
        }

        public Task<TmdbSeasonDetail?> GetSeasonAsync(
            int tvTmdbId,
            int seasonNumber,
            TmdbSettings settings,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<TmdbSeasonDetail?>(null);
        }

        public Task<string?> DownloadPosterAsync(
            string posterPath,
            string mediaType,
            int tmdbId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<string?> DownloadStillAsync(
            string stillPath,
            int tvTmdbId,
            int seasonNumber,
            int episodeNumber,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }
    }
}
