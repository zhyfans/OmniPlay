using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.Library;
using Xunit;

namespace OmniPlay.Tests;

public sealed class BackgroundTaskCenterTests
{
    [Fact]
    public async Task TryStartExclusiveRunsOneTaskAtATimeAndStoresResult()
    {
        var center = new InMemoryBackgroundTaskCenter();
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(center.TryStartExclusive(
            "scan",
            "扫描媒体库",
            (_, progress, cancellationToken) =>
            {
                progress.Report(new BackgroundTaskProgress("running", "处理中", 25, "item-1"));
                return completion.Task.WaitAsync(cancellationToken);
            },
            onAccepted: null,
            out var running));
        Assert.True(running.IsRunning);
        Assert.False(center.TryStartExclusive(
            "scrape",
            "刮削媒体库",
            (_, _, _) => Task.FromResult<string?>("done"),
            onAccepted: null,
            out var rejected));
        Assert.Equal(running.Id, rejected.Id);

        completion.SetResult("完成");
        var completed = await WaitForSnapshotAsync(center, static snapshot => snapshot.ActiveTask is null);

        Assert.Null(completed.ActiveTask);
        Assert.Equal("completed", completed.Tasks[0].State);
        Assert.Equal("完成", completed.Tasks[0].ResultText);
    }

    [Fact]
    public async Task TryCancelCancelsRunningTask()
    {
        var center = new InMemoryBackgroundTaskCenter();
        Assert.True(center.TryStartExclusive(
            "scan",
            "扫描媒体库",
            async (_, _, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                return "done";
            },
            onAccepted: null,
            out var running));

        Assert.True(center.TryCancel(running.Id, out var canceling));
        Assert.True(canceling.IsCancellationRequested);
        var snapshot = await WaitForSnapshotAsync(center, static next => next.ActiveTask is null);

        Assert.Equal("canceled", snapshot.Tasks[0].State);
    }

    private static async Task<BackgroundTaskSnapshot> WaitForSnapshotAsync(
        InMemoryBackgroundTaskCenter center,
        Func<BackgroundTaskSnapshot, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!timeout.IsCancellationRequested)
        {
            var snapshot = center.GetSnapshot();
            if (predicate(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(10, timeout.Token);
        }

        throw new TimeoutException("Timed out waiting for background task snapshot.");
    }
}
