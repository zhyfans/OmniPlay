using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Core.Runtime;

namespace OmniPlay.Infrastructure.FileSystem;

public sealed class CacheUsageService : ICacheUsageService
{
    private readonly IStoragePaths storagePaths;
    private readonly IAppSettingsRepository? appSettingsRepository;

    public CacheUsageService(IStoragePaths storagePaths, IAppSettingsRepository? appSettingsRepository = null)
    {
        this.storagePaths = storagePaths;
        this.appSettingsRepository = appSettingsRepository;
    }

    public CacheUsageSummary GetUsage()
    {
        storagePaths.EnsureCreated();
        var cacheSettings = ResolveCacheSettings();
        var buckets = new[]
        {
            Measure("posters", "海报", storagePaths.PostersDirectory),
            Measure("thumbnails", "剧照", storagePaths.ThumbnailsDirectory),
            Measure("webdav", "WebDAV", Path.Combine(storagePaths.CacheDirectory, "webdav")),
            Measure("transcode", "HLS", HlsCachePathResolver.ResolveRoot(storagePaths, cacheSettings)),
            Measure("subtitles", "字幕", SubtitleCachePathResolver.ResolveRoot(storagePaths, cacheSettings))
        };

        return new CacheUsageSummary(
            buckets.Sum(static bucket => bucket.Bytes),
            buckets.Sum(static bucket => bucket.FileCount),
            buckets,
            DateTimeOffset.UtcNow);
    }

    private CacheSettings ResolveCacheSettings()
    {
        if (appSettingsRepository is null)
        {
            return new CacheSettings();
        }

        try
        {
            return appSettingsRepository.GetAsync().GetAwaiter().GetResult().Cache;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CacheSettings();
        }
    }

    private static CacheUsageBucket Measure(string key, string label, string path)
    {
        if (!Directory.Exists(path))
        {
            return new CacheUsageBucket(key, label, path, 0, 0);
        }

        var bytes = 0L;
        var fileCount = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(file);
                bytes += info.Length;
                fileCount++;
            }
            catch (IOException)
            {
                // Cache usage is best-effort; transient file races should not fail the UI.
            }
            catch (UnauthorizedAccessException)
            {
                // Keep the rest of the cache report available if one file cannot be read.
            }
        }

        return new CacheUsageBucket(key, label, path, bytes, fileCount);
    }
}
