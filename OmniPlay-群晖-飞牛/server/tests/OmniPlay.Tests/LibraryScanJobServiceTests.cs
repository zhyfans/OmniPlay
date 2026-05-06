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
        var service = new LibraryScanJobService(new InMemoryBackgroundTaskCenter(), scanner, statusStore);

        Assert.True(service.TryStartScan(out var started));
        Assert.True(started.IsRunning);
        Assert.False(service.TryStartScan(out var duplicate));
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
        var service = new LibraryScanJobService(new InMemoryBackgroundTaskCenter(), scanner, statusStore);

        Assert.True(service.TryStartScan(out _));
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

        public Task WaitUntilStartedAsync()
        {
            return started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }

        public void Complete(LibraryScanSummary summary)
        {
            completion.TrySetResult(summary);
        }
    }
}
