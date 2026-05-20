using OmniPlay.Infrastructure.FileSystem;
using Xunit;

namespace OmniPlay.Tests;

public sealed class WebDavCacheCleanupServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omniplay-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CleanupRemovesOnlyExpiredWebDavCacheFiles()
    {
        var paths = new StoragePaths(Path.Combine(root, "app"));
        paths.EnsureCreated();
        var cacheRoot = Path.Combine(paths.CacheDirectory, "webdav");
        var expired = Touch(Path.Combine(cacheRoot, "old.mkv"), [1, 2, 3], DateTime.UtcNow.AddHours(-10));
        var fresh = Touch(Path.Combine(cacheRoot, "fresh.mkv"), [4, 5], DateTime.UtcNow);

        var summary = await new WebDavCacheCleanupService(paths).CleanupAsync(TimeSpan.FromHours(2));

        Assert.Equal(1, summary.RemovedFileCount);
        Assert.Equal(3, summary.RemovedBytes);
        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task CleanupEvictsOldestFilesWhenWebDavCacheExceedsLimit()
    {
        var paths = new StoragePaths(Path.Combine(root, "capacity"));
        paths.EnsureCreated();
        var cacheRoot = Path.Combine(paths.CacheDirectory, "webdav");
        var oldest = Touch(Path.Combine(cacheRoot, "ranges", "a", "old.seg"), [1, 2, 3], DateTime.UtcNow.AddHours(-3));
        var middle = Touch(Path.Combine(cacheRoot, "ranges", "a", "middle.seg"), [4, 5, 6], DateTime.UtcNow.AddHours(-2));
        var newest = Touch(Path.Combine(cacheRoot, "fresh.mkv"), [7, 8, 9], DateTime.UtcNow.AddHours(-1));

        var summary = await new WebDavCacheCleanupService(paths).CleanupAsync(
            TimeSpan.FromHours(24),
            maxBytes: 5);

        Assert.Equal(2, summary.RemovedFileCount);
        Assert.Equal(6, summary.RemovedBytes);
        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(middle));
        Assert.True(File.Exists(newest));
    }

    private static string Touch(string path, byte[] bytes, DateTime timestampUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, timestampUtc);
        File.SetLastAccessTimeUtc(path, timestampUtc);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
