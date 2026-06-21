using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
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
        Touch(Path.Combine(paths.CacheDirectory, "subtitles", "sup", "track.sup"), [10, 11]);

        var usage = new CacheUsageService(paths).GetUsage();

        Assert.Equal(11, usage.TotalBytes);
        Assert.Equal(4, usage.TotalFileCount);
        Assert.Equal(5, usage.Buckets.Count);
        Assert.Equal(3, usage.Buckets.Single(bucket => bucket.Key == "posters").Bytes);
        Assert.Equal(2, usage.Buckets.Single(bucket => bucket.Key == "thumbnails").Bytes);
        Assert.Equal(0, usage.Buckets.Single(bucket => bucket.Key == "webdav").Bytes);
        Assert.Equal(4, usage.Buckets.Single(bucket => bucket.Key == "transcode").Bytes);
        Assert.Equal(2, usage.Buckets.Single(bucket => bucket.Key == "subtitles").Bytes);
    }

    [Fact]
    public void GetUsageMeasuresConfiguredHlsAndSubtitleCacheDirectories()
    {
        var paths = new StoragePaths(Path.Combine(root, "custom-app"));
        paths.EnsureCreated();
        var hlsPath = Path.Combine(root, "external-hls");
        var subtitlePath = Path.Combine(root, "external-subtitles");
        Touch(Path.Combine(hlsPath, "session", "index.m3u8"), [1, 2, 3, 4, 5]);
        Touch(Path.Combine(subtitlePath, "sup", "track.sup"), [6, 7, 8]);
        var settings = new AppSettingsSnapshot(
            "OmniPlay",
            "phase-2",
            new TmdbSettings(),
            new CacheSettings(HlsCachePath: hlsPath, SubtitleCachePath: subtitlePath),
            new PlaybackSettings(),
            new ProxySettings(),
            new AutomationSettings());

        var usage = new CacheUsageService(paths, new FixedSettingsRepository(settings)).GetUsage();

        var hlsBucket = usage.Buckets.Single(bucket => bucket.Key == "transcode");
        var subtitleBucket = usage.Buckets.Single(bucket => bucket.Key == "subtitles");
        Assert.Equal(hlsPath, hlsBucket.Path);
        Assert.Equal(5, hlsBucket.Bytes);
        Assert.Equal(subtitlePath, subtitleBucket.Path);
        Assert.Equal(3, subtitleBucket.Bytes);
    }

    private static void Touch(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private sealed class FixedSettingsRepository : IAppSettingsRepository
    {
        private readonly AppSettingsSnapshot snapshot;

        public FixedSettingsRepository(AppSettingsSnapshot snapshot)
        {
            this.snapshot = snapshot;
        }

        public Task<AppSettingsSnapshot> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(snapshot);
        }

        public Task<AppSettingsSnapshot> UpdateAsync(
            AppSettingsUpdateRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(snapshot);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
