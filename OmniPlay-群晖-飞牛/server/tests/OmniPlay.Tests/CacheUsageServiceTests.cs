using OmniPlay.Infrastructure.FileSystem;
using Xunit;

namespace OmniPlay.Tests;

public sealed class CacheUsageServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omniplay-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void GetUsageReturnsPerBucketAndTotalCacheSize()
    {
        var paths = new StoragePaths(Path.Combine(root, "app"));
        paths.EnsureCreated();
        Touch(Path.Combine(paths.PostersDirectory, "poster.jpg"), [1, 2, 3]);
        Touch(Path.Combine(paths.ThumbnailsDirectory, "still.jpg"), [4, 5]);
        Touch(Path.Combine(paths.TranscodeDirectory, "session", "index.m3u8"), [6, 7, 8, 9]);

        var usage = new CacheUsageService(paths).GetUsage();

        Assert.Equal(9, usage.TotalBytes);
        Assert.Equal(3, usage.TotalFileCount);
        Assert.Equal(4, usage.Buckets.Count);
        Assert.Equal(3, usage.Buckets.Single(bucket => bucket.Key == "posters").Bytes);
        Assert.Equal(2, usage.Buckets.Single(bucket => bucket.Key == "thumbnails").Bytes);
        Assert.Equal(0, usage.Buckets.Single(bucket => bucket.Key == "webdav").Bytes);
        Assert.Equal(4, usage.Buckets.Single(bucket => bucket.Key == "transcode").Bytes);
    }

    private static void Touch(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
