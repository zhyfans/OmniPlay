using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.FileSystem;

public sealed class WebDavCacheCleanupService : IWebDavCacheCleanupService
{
    private readonly IStoragePaths storagePaths;

    public WebDavCacheCleanupService(IStoragePaths storagePaths)
    {
        this.storagePaths = storagePaths;
    }

    public Task<WebDavCacheCleanupSummary> CleanupAsync(
        TimeSpan maxAge,
        IProgress<BackgroundTaskProgress>? progress = null,
        CancellationToken cancellationToken = default,
        long? maxBytes = null)
    {
        var cacheDirectory = Path.Combine(storagePaths.CacheDirectory, "webdav");
        if (!Directory.Exists(cacheDirectory))
        {
            return Task.FromResult(new WebDavCacheCleanupSummary(0, 0));
        }

        var files = Directory
            .EnumerateFiles(cacheDirectory, "*", SearchOption.AllDirectories)
            .ToArray();
        var threshold = DateTimeOffset.UtcNow - maxAge;
        var removedFiles = 0;
        long removedBytes = 0;
        List<CacheFileSnapshot> retainedFiles = [];
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            progress?.Report(new BackgroundTaskProgress(
                "cleanup",
                $"正在清理 WebDAV 缓存 {index + 1}/{files.Length}",
                files.Length == 0 ? 100 : (double)(index + 1) / files.Length * 100,
                Path.GetFileName(file)));

            try
            {
                var info = new FileInfo(file);
                var lastUsedAt = info.LastAccessTimeUtc > info.LastWriteTimeUtc
                    ? info.LastAccessTimeUtc
                    : info.LastWriteTimeUtc;
                if (lastUsedAt > threshold.UtcDateTime)
                {
                    retainedFiles.Add(new CacheFileSnapshot(file, info.Length, lastUsedAt));
                    continue;
                }

                var length = info.Length;
                info.Delete();
                removedFiles++;
                removedBytes += length;
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        if (maxBytes is > 0)
        {
            var retainedBytes = retainedFiles.Sum(static file => file.Length);
            foreach (var file in retainedFiles.OrderBy(static file => file.LastUsedAtUtc))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (retainedBytes <= maxBytes.Value)
                {
                    break;
                }

                progress?.Report(new BackgroundTaskProgress(
                    "capacity-cleanup",
                    $"正在按容量清理 WebDAV 缓存 {FormatBytes(retainedBytes)} / {FormatBytes(maxBytes.Value)}",
                    null,
                    Path.GetFileName(file.Path)));

                try
                {
                    var info = new FileInfo(file.Path);
                    if (!info.Exists)
                    {
                        retainedBytes -= file.Length;
                        continue;
                    }

                    var length = info.Length;
                    info.Delete();
                    retainedBytes -= length;
                    removedFiles++;
                    removedBytes += length;
                }
                catch (FileNotFoundException)
                {
                    retainedBytes -= file.Length;
                }
                catch (DirectoryNotFoundException)
                {
                    retainedBytes -= file.Length;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        RemoveEmptyDirectories(cacheDirectory);
        return Task.FromResult(new WebDavCacheCleanupSummary(removedFiles, removedBytes));
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024d / 1024d:0.##} GB";
        }

        return bytes >= 1024L * 1024
            ? $"{bytes / 1024d / 1024d:0.##} MB"
            : $"{bytes / 1024d:0.##} KB";
    }

    private static void RemoveEmptyDirectories(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(static path => path.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record CacheFileSnapshot(
        string Path,
        long Length,
        DateTime LastUsedAtUtc);
}
