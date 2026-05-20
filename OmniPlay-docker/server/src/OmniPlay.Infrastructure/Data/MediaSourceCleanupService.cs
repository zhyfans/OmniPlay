using Microsoft.Data.Sqlite;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.Data;

public sealed class MediaSourceCleanupService : IMediaSourceCleanupService
{
    private readonly SqliteDatabase database;

    public MediaSourceCleanupService(SqliteDatabase database)
    {
        this.database = database;
    }

    public async Task<MediaSourceCleanupSummary> CleanupRemovedSourceAsync(
        long sourceId,
        IProgress<BackgroundTaskProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        progress?.Report(new BackgroundTaskProgress("scan-source", "正在定位媒体源库数据", 10, null));
        await PrepareCleanupTablesAsync(connection, transaction, sourceId, cancellationToken);
        var videoFileCount = await CountTempRowsAsync(connection, transaction, "cleanup_video_ids", cancellationToken);
        if (videoFileCount == 0)
        {
            transaction.Commit();
            progress?.Report(new BackgroundTaskProgress("completed", "媒体源没有关联视频文件", 100, null));
            return new MediaSourceCleanupSummary(0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new BackgroundTaskProgress("cleanup-progress", "正在清理播放进度和缓存索引", 25, null));
        var removedPlaybackProgress = await ExecuteAsync(connection, transaction, """
            DELETE FROM playback_progress
            WHERE video_file_id IN (SELECT id FROM cleanup_video_ids);
            """, cancellationToken);
        await ExecuteAsync(connection, transaction, """
            UPDATE thumbnail_assets
            SET video_file_id = NULL
            WHERE video_file_id IN (SELECT id FROM cleanup_video_ids);
            """, cancellationToken);
        var removedTranscodeJobs = await ExecuteAsync(connection, transaction, """
            DELETE FROM transcode_jobs
            WHERE video_file_id IN (SELECT id FROM cleanup_video_ids);
            """, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new BackgroundTaskProgress("cleanup-files", "正在移除视频文件记录", 45, null));
        var removedVideoFiles = await ExecuteAsync(connection, transaction, """
            DELETE FROM video_files
            WHERE id IN (SELECT id FROM cleanup_video_ids);
            """, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new BackgroundTaskProgress("cleanup-episodes", "正在清理无文件分集", 65, null));
        var removedOrphanEpisodes = await ExecuteAsync(connection, transaction, """
            DELETE FROM episodes
            WHERE id IN (SELECT id FROM cleanup_episode_ids)
              AND NOT EXISTS (
                  SELECT 1
                  FROM video_files vf
                  WHERE vf.episode_id = episodes.id
                    AND vf.missing_at IS NULL
              );
            """, cancellationToken);
        var removedOrphanSeasons = await ExecuteAsync(connection, transaction, """
            DELETE FROM seasons
            WHERE tv_show_id IN (
                SELECT ts.id
                FROM tv_shows ts
                JOIN cleanup_library_item_ids cli ON cli.id = ts.library_item_id
            )
              AND NOT EXISTS (
                  SELECT 1
                  FROM episodes e
                  WHERE e.season_id = seasons.id
              );
            """, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new BackgroundTaskProgress("cleanup-items", "正在清理无文件影视条目", 82, null));
        await ExecuteAsync(connection, transaction, """
            CREATE TEMP TABLE cleanup_delete_library_item_ids (
                id TEXT PRIMARY KEY
            );
            """, cancellationToken);
        await ExecuteAsync(connection, transaction, "DELETE FROM cleanup_delete_library_item_ids;", cancellationToken);
        await ExecuteAsync(connection, transaction, """
            INSERT OR IGNORE INTO cleanup_delete_library_item_ids (id)
            SELECT li.id
            FROM library_items li
            JOIN cleanup_library_item_ids cli ON cli.id = li.id
            WHERE NOT EXISTS (
                SELECT 1
                FROM video_files vf
                WHERE vf.library_item_id = li.id
                  AND vf.missing_at IS NULL
            );
            """, cancellationToken);

        var removedItemEpisodes = await ExecuteAsync(connection, transaction, """
            DELETE FROM episodes
            WHERE season_id IN (
                SELECT s.id
                FROM seasons s
                JOIN tv_shows ts ON ts.id = s.tv_show_id
                JOIN cleanup_delete_library_item_ids dli ON dli.id = ts.library_item_id
            );
            """, cancellationToken);
        var removedItemSeasons = await ExecuteAsync(connection, transaction, """
            DELETE FROM seasons
            WHERE tv_show_id IN (
                SELECT ts.id
                FROM tv_shows ts
                JOIN cleanup_delete_library_item_ids dli ON dli.id = ts.library_item_id
            );
            """, cancellationToken);
        var removedTvShows = await ExecuteAsync(connection, transaction, """
            DELETE FROM tv_shows
            WHERE library_item_id IN (SELECT id FROM cleanup_delete_library_item_ids);
            """, cancellationToken);
        var removedMovies = await ExecuteAsync(connection, transaction, """
            DELETE FROM movies
            WHERE library_item_id IN (SELECT id FROM cleanup_delete_library_item_ids);
            """, cancellationToken);
        var removedLibraryItems = await ExecuteAsync(connection, transaction, """
            DELETE FROM library_items
            WHERE id IN (SELECT id FROM cleanup_delete_library_item_ids);
            """, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        progress?.Report(new BackgroundTaskProgress("completed", "媒体源库清理完成", 100, null));

        return new MediaSourceCleanupSummary(
            removedVideoFiles,
            removedPlaybackProgress,
            RemovedThumbnailAssetRecordCount: 0,
            removedTranscodeJobs,
            removedOrphanEpisodes + removedItemEpisodes,
            removedOrphanSeasons + removedItemSeasons,
            removedTvShows,
            removedMovies,
            removedLibraryItems);
    }

    private static async Task PrepareCleanupTablesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sourceId,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            CREATE TEMP TABLE cleanup_video_ids (
                id TEXT PRIMARY KEY
            );
            """, cancellationToken);
        await ExecuteAsync(connection, transaction, """
            CREATE TEMP TABLE cleanup_library_item_ids (
                id TEXT PRIMARY KEY
            );
            """, cancellationToken);
        await ExecuteAsync(connection, transaction, """
            CREATE TEMP TABLE cleanup_episode_ids (
                id TEXT PRIMARY KEY
            );
            """, cancellationToken);
        await ExecuteAsync(connection, transaction, "DELETE FROM cleanup_video_ids;", cancellationToken);
        await ExecuteAsync(connection, transaction, "DELETE FROM cleanup_library_item_ids;", cancellationToken);
        await ExecuteAsync(connection, transaction, "DELETE FROM cleanup_episode_ids;", cancellationToken);
        await ExecuteAsync(connection, transaction, """
            INSERT OR IGNORE INTO cleanup_video_ids (id)
            SELECT id
            FROM video_files
            WHERE source_id = $sourceId;
            """, cancellationToken, ("$sourceId", sourceId));
        await ExecuteAsync(connection, transaction, """
            INSERT OR IGNORE INTO cleanup_library_item_ids (id)
            SELECT DISTINCT library_item_id
            FROM video_files
            WHERE source_id = $sourceId
              AND library_item_id IS NOT NULL;
            """, cancellationToken, ("$sourceId", sourceId));
        await ExecuteAsync(connection, transaction, """
            INSERT OR IGNORE INTO cleanup_episode_ids (id)
            SELECT DISTINCT episode_id
            FROM video_files
            WHERE source_id = $sourceId
              AND episode_id IS NOT NULL;
            """, cancellationToken, ("$sourceId", sourceId));
    }

    private static async Task<int> CountTempRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value);
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
