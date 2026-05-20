using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.Library;
using Xunit;

namespace OmniPlay.Tests;

public sealed class LibraryMetadataEnrichmentJobServiceTests
{
    [Fact]
    public async Task TryStartMissingRunsEnrichmentInBackgroundAndStoresSummary()
    {
        var enricher = new ControlledEnricher();
        var statusStore = new InMemoryMetadataEnrichmentStatusStore();
        var service = new LibraryMetadataEnrichmentJobService(new InMemoryBackgroundTaskCenter(), enricher, statusStore);

        Assert.True(service.TryStartMissing(out var started));
        Assert.True(started.IsRunning);
        Assert.False(service.TryStartMissing(out var duplicate));
        Assert.True(duplicate.IsRunning);

        await enricher.WaitUntilStartedAsync();
        enricher.Complete(new LibraryMetadataEnrichmentSummary(ScannedItems: 2, MatchedItems: 2, UpdatedItems: 1));
        var completed = await WaitForStatusAsync(statusStore, static status => !status.IsRunning);

        Assert.False(completed.WasCanceled);
        Assert.NotNull(completed.LastSummary);
        Assert.Equal(2, completed.LastSummary.MatchedItems);
        Assert.Equal(1, completed.LastSummary.UpdatedItems);
    }

    [Fact]
    public async Task TryStartItemRecordsTargetLibraryItemId()
    {
        var enricher = new ControlledEnricher();
        var statusStore = new InMemoryMetadataEnrichmentStatusStore();
        var service = new LibraryMetadataEnrichmentJobService(new InMemoryBackgroundTaskCenter(), enricher, statusStore);

        Assert.True(service.TryStartItem("item-1", out var started));
        Assert.Equal("item-1", started.TargetLibraryItemId);

        await enricher.WaitUntilStartedAsync();
        enricher.Complete(new LibraryMetadataEnrichmentSummary(ScannedItems: 1, MatchedItems: 1, UpdatedItems: 1));
        var completed = await WaitForStatusAsync(statusStore, static status => !status.IsRunning);

        Assert.Equal("item-1", completed.TargetLibraryItemId);
        Assert.Equal("item-1", enricher.LastItemId);
    }

    [Fact]
    public async Task RequestCancelCancelsRunningEnrichment()
    {
        var enricher = new ControlledEnricher();
        var statusStore = new InMemoryMetadataEnrichmentStatusStore();
        var service = new LibraryMetadataEnrichmentJobService(new InMemoryBackgroundTaskCenter(), enricher, statusStore);

        Assert.True(service.TryStartMissing(out _));
        await enricher.WaitUntilStartedAsync();
        Assert.True(service.RequestCancel(out var canceling));
        Assert.True(canceling.IsCancellationRequested || canceling.WasCanceled);

        var canceled = await WaitForStatusAsync(statusStore, static status => !status.IsRunning);

        Assert.True(canceled.WasCanceled);
        Assert.Null(canceled.LastError);
    }

    private static async Task<LibraryMetadataEnrichmentStatus> WaitForStatusAsync(
        IMetadataEnrichmentStatusStore statusStore,
        Func<LibraryMetadataEnrichmentStatus, bool> predicate)
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

        throw new TimeoutException("Timed out waiting for metadata enrichment status.");
    }

    private sealed class ControlledEnricher : ILibraryMetadataEnricher
    {
        private readonly TaskCompletionSource<LibraryMetadataEnrichmentSummary> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? LastItemId { get; private set; }

        public Task<LibraryMetadataEnrichmentSummary> EnrichMissingAsync(CancellationToken cancellationToken = default)
        {
            return EnrichMissingAsync(null, cancellationToken);
        }

        public async Task<LibraryMetadataEnrichmentSummary> EnrichMissingAsync(
            IProgress<LibraryMetadataEnrichmentProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            return await EnrichMissingAsync(progress, new LibraryRefreshRequest(), cancellationToken);
        }

        public async Task<LibraryMetadataEnrichmentSummary> EnrichMissingAsync(
            IProgress<LibraryMetadataEnrichmentProgress>? progress,
            LibraryRefreshRequest order,
            CancellationToken cancellationToken = default)
        {
            ReportStarted(progress);
            return await completion.Task.WaitAsync(cancellationToken);
        }

        public Task<LibraryMetadataEnrichmentSummary> EnrichItemAsync(
            string libraryItemId,
            CancellationToken cancellationToken = default)
        {
            return EnrichItemAsync(libraryItemId, null, cancellationToken);
        }

        public async Task<LibraryMetadataEnrichmentSummary> EnrichItemAsync(
            string libraryItemId,
            IProgress<LibraryMetadataEnrichmentProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            LastItemId = libraryItemId;
            ReportStarted(progress);
            return await completion.Task.WaitAsync(cancellationToken);
        }

        public Task WaitUntilStartedAsync()
        {
            return started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }

        public void Complete(LibraryMetadataEnrichmentSummary summary)
        {
            completion.TrySetResult(summary);
        }

        private void ReportStarted(IProgress<LibraryMetadataEnrichmentProgress>? progress)
        {
            progress?.Report(new LibraryMetadataEnrichmentProgress(
                "searching",
                TargetItemCount: 1,
                ProcessedItemCount: 0,
                MatchedItemCount: 0,
                UpdatedItemCount: 0,
                DownloadedPosterCount: 0,
                CurrentItemId: "item-1",
                CurrentTitle: "测试条目",
                DateTimeOffset.UtcNow));
            started.SetResult();
        }
    }
}
