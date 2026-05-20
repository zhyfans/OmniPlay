using Microsoft.Data.Sqlite;
using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.FileSystem;
using Xunit;

namespace OmniPlay.Tests;

public sealed class AssetCacheCleanupServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omniplay-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CleanupOrphansRemovesUnreferencedAssetsAndUntrackedFiles()
    {
        var paths = new StoragePaths(Path.Combine(root, "app"));
        var database = new SqliteDatabase(paths);
        database.EnsureInitialized();
        paths.EnsureCreated();

        var livePoster = Touch(Path.Combine(paths.PostersDirectory, "live-poster.jpg"), [1, 2, 3]);
        var orphanPoster = Touch(Path.Combine(paths.PostersDirectory, "orphan-poster.jpg"), [4, 5, 6, 7]);
        var strayPoster = Touch(Path.Combine(paths.PostersDirectory, "stray-poster.jpg"), [8, 9]);
        var liveThumbnail = Touch(Path.Combine(paths.ThumbnailsDirectory, "live-thumb.jpg"), [10, 11, 12]);
        var orphanThumbnail = Touch(Path.Combine(paths.ThumbnailsDirectory, "orphan-thumb.jpg"), [13, 14, 15, 16]);
        var strayThumbnail = Touch(Path.Combine(paths.ThumbnailsDirectory, "stray-thumb.jpg"), [17, 18]);

        using (var connection = database.OpenConnection())
        {
            await ExecuteAsync(connection, """
                INSERT INTO poster_assets (id, remote_path, local_path, width, height, created_at)
                VALUES ('poster-live', NULL, $livePoster, NULL, NULL, $now),
                       ('poster-orphan', NULL, $orphanPoster, NULL, NULL, $now);

                INSERT INTO thumbnail_assets (id, video_file_id, local_path, width, height, created_at)
                VALUES ('thumb-live', NULL, $liveThumbnail, NULL, NULL, $now),
                       ('thumb-orphan', NULL, $orphanThumbnail, NULL, NULL, $now);

                INSERT INTO library_items (
                    id, item_kind, title, sort_title, release_date, overview, poster_asset_id, vote_average, is_locked, created_at, updated_at)
                VALUES ('item-tv', 'tv', '测试剧集', '测试剧集', NULL, NULL, 'poster-live', NULL, 0, $now, $now);

                INSERT INTO tv_shows (id, library_item_id, tmdb_id, original_name, first_air_date)
                VALUES ('show-tv', 'item-tv', 1, NULL, NULL);

                INSERT INTO seasons (id, tv_show_id, season_number, title, poster_asset_id)
                VALUES ('season-1', 'show-tv', 1, '第 1 季', NULL);

                INSERT INTO episodes (id, season_id, episode_number, title, overview, still_asset_id, air_date)
                VALUES ('episode-1', 'season-1', 1, '第 1 集', NULL, 'thumb-live', NULL);
                """,
                ("$livePoster", livePoster),
                ("$orphanPoster", orphanPoster),
                ("$liveThumbnail", liveThumbnail),
                ("$orphanThumbnail", orphanThumbnail),
                ("$now", DateTimeOffset.UtcNow.ToString("O")));
        }

        var summary = await new AssetCacheCleanupService(database, paths).CleanupOrphansAsync();

        Assert.Equal(4, summary.ScannedAssetCount);
        Assert.Equal(2, summary.RemovedAssetRecordCount);
        Assert.Equal(4, summary.RemovedFileCount);
        Assert.Equal(12, summary.RemovedBytes);
        Assert.True(File.Exists(livePoster));
        Assert.True(File.Exists(liveThumbnail));
        Assert.False(File.Exists(orphanPoster));
        Assert.False(File.Exists(strayPoster));
        Assert.False(File.Exists(orphanThumbnail));
        Assert.False(File.Exists(strayThumbnail));

        using var verifyConnection = database.OpenConnection();
        Assert.Equal(1, await CountAsync(verifyConnection, "poster_assets"));
        Assert.Equal(1, await CountAsync(verifyConnection, "thumbnail_assets"));
    }

    [Fact]
    public async Task CleanupOrphansOnlyPreservesUntrackedFiles()
    {
        var paths = new StoragePaths(Path.Combine(root, "app"));
        var database = new SqliteDatabase(paths);
        database.EnsureInitialized();
        paths.EnsureCreated();

        var orphanPoster = Touch(Path.Combine(paths.PostersDirectory, "orphan-poster.jpg"), [1, 2, 3]);
        var strayPoster = Touch(Path.Combine(paths.PostersDirectory, "stray-poster.jpg"), [4, 5]);

        using (var connection = database.OpenConnection())
        {
            await ExecuteAsync(connection, """
                INSERT INTO poster_assets (id, remote_path, local_path, width, height, created_at)
                VALUES ('poster-orphan', NULL, $orphanPoster, NULL, NULL, $now);
                """,
                ("$orphanPoster", orphanPoster),
                ("$now", DateTimeOffset.UtcNow.ToString("O")));
        }

        var summary = await new AssetCacheCleanupService(database, paths)
            .CleanupOrphansAsync(new AssetCacheCleanupOptions(IncludeUntrackedFiles: false));

        Assert.Equal(1, summary.RemovedAssetRecordCount);
        Assert.Equal(1, summary.RemovedFileCount);
        Assert.False(File.Exists(orphanPoster));
        Assert.True(File.Exists(strayPoster));
    }

    private static string Touch(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountAsync(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
