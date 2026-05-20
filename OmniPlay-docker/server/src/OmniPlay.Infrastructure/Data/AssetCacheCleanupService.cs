using Microsoft.Data.Sqlite;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.Data;

public sealed class AssetCacheCleanupService : IAssetCacheCleanupService
{
    private readonly SqliteDatabase database;
    private readonly IStoragePaths storagePaths;

    public AssetCacheCleanupService(SqliteDatabase database, IStoragePaths storagePaths)
    {
        this.database = database;
        this.storagePaths = storagePaths;
    }

    public async Task<AssetCacheCleanupSummary> CleanupOrphansAsync(
        AssetCacheCleanupOptions? options = null,
        IProgress<BackgroundTaskProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new AssetCacheCleanupOptions();
        storagePaths.EnsureCreated();
        progress?.Report(new BackgroundTaskProgress("scan-assets", "正在扫描图片缓存", 5, null));

        using var connection = database.OpenConnection();
        var posterAssets = await LoadPosterAssetsAsync(connection, cancellationToken);
        var thumbnailAssets = await LoadThumbnailAssetsAsync(connection, cancellationToken);
        var scannedAssetCount = posterAssets.Count + thumbnailAssets.Count;
        var removedRecords = 0;
        var removedFiles = 0;
        var removedBytes = 0L;

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new BackgroundTaskProgress("cleanup-assets", "正在清理孤儿海报", 35, null));
        var livePosterPaths = BuildLivePathSet(posterAssets);
        var orphanPosterAssets = posterAssets.Where(static asset => !asset.IsReferenced).ToArray();
        foreach (var asset in orphanPosterAssets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await DeletePosterAssetRecordAsync(connection, asset.Id, cancellationToken))
            {
                removedRecords++;
            }

            if (!livePosterPaths.Contains(NormalizePath(asset.LocalPath))
                && TryDeleteFile(asset.LocalPath, storagePaths.PostersDirectory, out var removedFileBytes))
            {
                removedFiles++;
                removedBytes += removedFileBytes;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new BackgroundTaskProgress("cleanup-assets", "正在清理孤儿剧照", 65, null));
        var liveThumbnailPaths = BuildLivePathSet(thumbnailAssets);
        var orphanThumbnailAssets = thumbnailAssets.Where(static asset => !asset.IsReferenced).ToArray();
        foreach (var asset in orphanThumbnailAssets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await DeleteThumbnailAssetRecordAsync(connection, asset.Id, cancellationToken))
            {
                removedRecords++;
            }

            if (!liveThumbnailPaths.Contains(NormalizePath(asset.LocalPath))
                && TryDeleteFile(asset.LocalPath, storagePaths.ThumbnailsDirectory, out var removedFileBytes))
            {
                removedFiles++;
                removedBytes += removedFileBytes;
            }
        }

        if (options.IncludeUntrackedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new BackgroundTaskProgress("cleanup-files", "正在清理残留图片文件", 85, null));
            var posterSweep = SweepUntrackedFiles(storagePaths.PostersDirectory, livePosterPaths, cancellationToken);
            var thumbnailSweep = SweepUntrackedFiles(storagePaths.ThumbnailsDirectory, liveThumbnailPaths, cancellationToken);
            removedFiles += posterSweep.RemovedFileCount + thumbnailSweep.RemovedFileCount;
            removedBytes += posterSweep.RemovedBytes + thumbnailSweep.RemovedBytes;
        }

        progress?.Report(new BackgroundTaskProgress("completed", "图片缓存清理完成", 100, null));
        return new AssetCacheCleanupSummary(scannedAssetCount, removedRecords, removedFiles, removedBytes);
    }

    private static async Task<IReadOnlyList<CachedAsset>> LoadPosterAssetsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pa.id,
                   pa.local_path,
                   CASE
                       WHEN EXISTS (SELECT 1 FROM library_items li WHERE li.poster_asset_id = pa.id)
                            OR EXISTS (SELECT 1 FROM seasons s WHERE s.poster_asset_id = pa.id) THEN 1
                       ELSE 0
                   END AS is_referenced
            FROM poster_assets pa;
            """;
        return await ReadAssetsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<CachedAsset>> LoadThumbnailAssetsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ta.id,
                   ta.local_path,
                   CASE
                       WHEN EXISTS (SELECT 1 FROM episodes e WHERE e.still_asset_id = ta.id)
                            OR EXISTS (
                                SELECT 1
                                FROM video_files vf
                                WHERE vf.id = ta.video_file_id
                                  AND vf.missing_at IS NULL
                            ) THEN 1
                       ELSE 0
                   END AS is_referenced
            FROM thumbnail_assets ta;
            """;
        return await ReadAssetsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<CachedAsset>> ReadAssetsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        List<CachedAsset> assets = [];
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            assets.Add(new CachedAsset(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2) == 1));
        }

        return assets;
    }

    private static async Task<bool> DeletePosterAssetRecordAsync(
        SqliteConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM poster_assets WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static async Task<bool> DeleteThumbnailAssetRecordAsync(
        SqliteConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM thumbnail_assets WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static HashSet<string> BuildLivePathSet(IReadOnlyList<CachedAsset> assets)
    {
        return assets
            .Where(static asset => asset.IsReferenced)
            .Select(static asset => NormalizePath(asset.LocalPath))
            .ToHashSet(PathComparer);
    }

    private static FileCleanupResult SweepUntrackedFiles(
        string rootDirectory,
        HashSet<string> livePaths,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return new FileCleanupResult(0, 0);
        }

        var removedFiles = 0;
        var removedBytes = 0L;
        foreach (var file in Directory.EnumerateFiles(rootDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedPath = NormalizePath(file);
            if (livePaths.Contains(normalizedPath))
            {
                continue;
            }

            if (TryDeleteFile(file, rootDirectory, out var fileBytes))
            {
                removedFiles++;
                removedBytes += fileBytes;
            }
        }

        return new FileCleanupResult(removedFiles, removedBytes);
    }

    private static bool TryDeleteFile(string path, string expectedRoot, out long removedBytes)
    {
        removedBytes = 0;
        if (!IsPathInsideRoot(expectedRoot, path) || !File.Exists(path))
        {
            return false;
        }

        var info = new FileInfo(path);
        removedBytes = info.Length;
        File.Delete(path);
        return true;
    }

    private static bool IsPathInsideRoot(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        return normalizedCandidate.Equals(normalizedRoot, comparison)
               || normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record CachedAsset(string Id, string LocalPath, bool IsReferenced);

    private sealed record FileCleanupResult(int RemovedFileCount, long RemovedBytes);
}
