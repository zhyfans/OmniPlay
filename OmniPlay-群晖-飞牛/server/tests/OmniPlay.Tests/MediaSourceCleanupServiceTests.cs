using Microsoft.Data.Sqlite;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.FileSystem;
using Xunit;

namespace OmniPlay.Tests;

public sealed class MediaSourceCleanupServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omniplay-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CleanupRemovedSourceDeletesOnlyThatSourceLibraryData()
    {
        var mediaRoot1 = Directory.CreateDirectory(Path.Combine(root, "media-a")).FullName;
        var mediaRoot2 = Directory.CreateDirectory(Path.Combine(root, "media-b")).FullName;
        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();
        var sources = new MediaSourceRepository(database);
        var source1 = await sources.AddLocalAsync("源 A", mediaRoot1);
        var source2 = await sources.AddLocalAsync("源 B", mediaRoot2);

        using (var connection = database.OpenConnection())
        {
            InsertLibraryItem(connection, "li-shared", "movie", "共享影片");
            InsertLibraryItem(connection, "li-movie", "movie", "仅源 A 影片");
            InsertMovie(connection, "movie-source-only", "li-movie");
            InsertVideoFile(connection, "vf-shared-a", source1.Id, "li-shared", null);
            InsertVideoFile(connection, "vf-shared-b", source2.Id, "li-shared", null);
            InsertVideoFile(connection, "vf-movie-a", source1.Id, "li-movie", null);
            InsertPlaybackProgress(connection, "vf-shared-a");
            InsertPlaybackProgress(connection, "vf-shared-b");
            InsertPlaybackProgress(connection, "vf-movie-a");
            InsertThumbnailAsset(connection, "thumb-a", "vf-movie-a");
            InsertTranscodeJob(connection, "job-a", "vf-movie-a");

            InsertLibraryItem(connection, "li-tv", "tv", "仅源 A 剧集");
            InsertTvHierarchy(connection, "li-tv", "show-a", "season-a", "episode-a");
            InsertVideoFile(connection, "vf-tv-a", source1.Id, "li-tv", "episode-a");
        }

        await sources.RemoveAsync(source1.Id);
        var summary = await new MediaSourceCleanupService(database).CleanupRemovedSourceAsync(source1.Id);

        Assert.Equal(3, summary.RemovedVideoFileCount);
        Assert.Equal(2, summary.RemovedPlaybackProgressCount);
        Assert.Equal(0, summary.RemovedThumbnailAssetRecordCount);
        Assert.Equal(1, summary.RemovedTranscodeJobCount);
        Assert.Equal(2, summary.RemovedLibraryItemCount);
        Assert.Equal(1, summary.RemovedMovieCount);
        Assert.Equal(1, summary.RemovedTvShowCount);

        using var verify = database.OpenConnection();
        Assert.Equal(0, CountRows(verify, "video_files", "source_id = $sourceId", ("$sourceId", source1.Id)));
        Assert.Equal(1, CountRows(verify, "video_files", "source_id = $sourceId", ("$sourceId", source2.Id)));
        Assert.Equal(1, CountRows(verify, "library_items", "id = 'li-shared'"));
        Assert.Equal(0, CountRows(verify, "library_items", "id IN ('li-movie', 'li-tv')"));
        Assert.Equal(0, CountRows(verify, "playback_progress", "video_file_id IN ('vf-shared-a', 'vf-movie-a', 'vf-tv-a')"));
        Assert.Equal(1, CountRows(verify, "playback_progress", "video_file_id = 'vf-shared-b'"));
        Assert.Equal(1, CountRows(verify, "thumbnail_assets", "id = 'thumb-a'"));
        Assert.Equal(0, CountRows(verify, "transcode_jobs", "id = 'job-a'"));
    }

    [Fact]
    public async Task AssetCleanupRemovesImagesOrphanedBySourceCleanup()
    {
        var mediaRoot = Directory.CreateDirectory(Path.Combine(root, "media")).FullName;
        var paths = new StoragePaths(Path.Combine(root, "app"));
        var database = new SqliteDatabase(paths);
        database.EnsureInitialized();
        paths.EnsureCreated();
        var posterPath = Path.Combine(paths.PostersDirectory, "poster.jpg");
        var thumbnailPath = Path.Combine(paths.ThumbnailsDirectory, "thumb.jpg");
        await File.WriteAllBytesAsync(posterPath, [1, 2, 3, 4]);
        await File.WriteAllBytesAsync(thumbnailPath, [5, 6, 7, 8, 9]);

        var sources = new MediaSourceRepository(database);
        var source = await sources.AddLocalAsync("源", mediaRoot);
        using (var connection = database.OpenConnection())
        {
            InsertPosterAsset(connection, "poster-a", posterPath);
            InsertLibraryItem(connection, "li-movie", "movie", "影片", "poster-a");
            InsertMovie(connection, "movie-a", "li-movie");
            InsertVideoFile(connection, "vf-movie", source.Id, "li-movie", null);
            InsertThumbnailAsset(connection, "thumb-a", "vf-movie", thumbnailPath);
        }

        await sources.RemoveAsync(source.Id);
        await new MediaSourceCleanupService(database).CleanupRemovedSourceAsync(source.Id);
        var assetSummary = await new AssetCacheCleanupService(database, paths).CleanupOrphansAsync();

        Assert.Equal(2, assetSummary.RemovedAssetRecordCount);
        Assert.Equal(2, assetSummary.RemovedFileCount);
        Assert.Equal(9, assetSummary.RemovedBytes);
        Assert.False(File.Exists(posterPath));
        Assert.False(File.Exists(thumbnailPath));
    }

    private static void InsertLibraryItem(
        SqliteConnection connection,
        string id,
        string kind,
        string title,
        string? posterAssetId = null)
    {
        Execute(connection, """
            INSERT INTO library_items (
                id, item_kind, title, sort_title, release_date, overview, poster_asset_id, vote_average, is_locked, created_at, updated_at)
            VALUES ($id, $kind, $title, $title, NULL, NULL, $posterAssetId, NULL, 0, $now, $now);
            """,
            ("$id", id),
            ("$kind", kind),
            ("$title", title),
            ("$posterAssetId", posterAssetId),
            ("$now", DateTimeOffset.UtcNow.ToString("O")));
    }

    private static void InsertPosterAsset(SqliteConnection connection, string id, string path)
    {
        Execute(connection, """
            INSERT INTO poster_assets (id, remote_path, local_path, width, height, created_at)
            VALUES ($id, NULL, $path, NULL, NULL, $now);
            """,
            ("$id", id),
            ("$path", path),
            ("$now", DateTimeOffset.UtcNow.ToString("O")));
    }

    private static void InsertMovie(SqliteConnection connection, string id, string libraryItemId)
    {
        Execute(connection, """
            INSERT INTO movies (id, library_item_id, tmdb_id, original_title, runtime_seconds)
            VALUES ($id, $libraryItemId, NULL, NULL, NULL);
            """,
            ("$id", id),
            ("$libraryItemId", libraryItemId));
    }

    private static void InsertTvHierarchy(
        SqliteConnection connection,
        string libraryItemId,
        string tvShowId,
        string seasonId,
        string episodeId)
    {
        Execute(connection, """
            INSERT INTO tv_shows (id, library_item_id, tmdb_id, original_name, first_air_date)
            VALUES ($id, $libraryItemId, NULL, NULL, NULL);
            """,
            ("$id", tvShowId),
            ("$libraryItemId", libraryItemId));
        Execute(connection, """
            INSERT INTO seasons (id, tv_show_id, season_number, title, poster_asset_id)
            VALUES ($id, $tvShowId, 1, '第 1 季', NULL);
            """,
            ("$id", seasonId),
            ("$tvShowId", tvShowId));
        Execute(connection, """
            INSERT INTO episodes (id, season_id, episode_number, title, overview, still_asset_id, air_date)
            VALUES ($id, $seasonId, 1, '第 1 集', NULL, NULL, NULL);
            """,
            ("$id", episodeId),
            ("$seasonId", seasonId));
    }

    private static void InsertVideoFile(SqliteConnection connection, string id, long sourceId, string libraryItemId, string? episodeId)
    {
        Execute(connection, """
            INSERT INTO video_files (
                id, source_id, library_item_id, episode_id, relative_path, file_name, file_size_bytes, modified_at,
                media_kind, duration_seconds, container, video_codec, audio_codec, subtitle_summary, probe_json,
                created_at, updated_at, missing_at)
            VALUES (
                $id, $sourceId, $libraryItemId, $episodeId, $relativePath, $fileName, 10, NULL,
                'movie', 10, NULL, NULL, NULL, NULL, NULL, $now, $now, NULL);
            """,
            ("$id", id),
            ("$sourceId", sourceId),
            ("$libraryItemId", libraryItemId),
            ("$episodeId", episodeId),
            ("$relativePath", $"{id}.mp4"),
            ("$fileName", $"{id}.mp4"),
            ("$now", DateTimeOffset.UtcNow.ToString("O")));
    }

    private static void InsertPlaybackProgress(SqliteConnection connection, string videoFileId)
    {
        Execute(connection, """
            INSERT INTO playback_progress (id, user_id, video_file_id, position_seconds, duration_seconds, is_watched, updated_at)
            VALUES ($id, 'local', $videoFileId, 5, 10, 0, $now);
            """,
            ("$id", $"pp-{videoFileId}"),
            ("$videoFileId", videoFileId),
            ("$now", DateTimeOffset.UtcNow.ToString("O")));
    }

    private static void InsertThumbnailAsset(SqliteConnection connection, string id, string videoFileId, string? path = null)
    {
        Execute(connection, """
            INSERT INTO thumbnail_assets (id, video_file_id, local_path, width, height, created_at)
            VALUES ($id, $videoFileId, $path, NULL, NULL, $now);
            """,
            ("$id", id),
            ("$videoFileId", videoFileId),
            ("$path", path ?? Path.Combine(Path.GetTempPath(), $"{id}.jpg")),
            ("$now", DateTimeOffset.UtcNow.ToString("O")));
    }

    private static void InsertTranscodeJob(SqliteConnection connection, string id, string videoFileId)
    {
        Execute(connection, """
            INSERT INTO transcode_jobs (id, video_file_id, status, profile, output_directory, created_at, updated_at, error_message)
            VALUES ($id, $videoFileId, 'ready', 'test', '/tmp/test', $now, $now, NULL);
            """,
            ("$id", id),
            ("$videoFileId", videoFileId),
            ("$now", DateTimeOffset.UtcNow.ToString("O")));
    }

    private static int CountRows(
        SqliteConnection connection,
        string table,
        string where,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {where};";
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
