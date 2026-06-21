using Microsoft.Data.Sqlite;
using OmniPlay.Core.Interfaces;

namespace OmniPlay.Infrastructure.Data;

public sealed class SqliteDatabase
{
    private readonly IStoragePaths storagePaths;

    public SqliteDatabase(IStoragePaths storagePaths)
    {
        this.storagePaths = storagePaths;
    }

    public string DatabasePath => Path.Combine(storagePaths.DataDirectory, "omniplay.sqlite");

    public void EnsureInitialized()
    {
        storagePaths.EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS schema_migrations (
                id TEXT PRIMARY KEY,
                applied_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS media_sources (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                kind TEXT NOT NULL,
                base_url TEXT NOT NULL,
                auth_reference TEXT NULL,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                disabled_at TEXT NULL,
                removed_at TEXT NULL,
                last_scanned_at TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS media_source_credentials (
                id TEXT PRIMARY KEY,
                source_id INTEGER NULL,
                kind TEXT NOT NULL,
                username TEXT NULL,
                secret_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY(source_id) REFERENCES media_sources(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS library_items (
                id TEXT PRIMARY KEY,
                item_kind TEXT NOT NULL,
                title TEXT NOT NULL,
                sort_title TEXT NOT NULL,
                release_date TEXT NULL,
                overview TEXT NULL,
                poster_asset_id TEXT NULL,
                vote_average REAL NULL,
                is_locked INTEGER NOT NULL DEFAULT 0,
                is_visible INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS movies (
                id TEXT PRIMARY KEY,
                library_item_id TEXT NOT NULL,
                tmdb_id INTEGER NULL,
                original_title TEXT NULL,
                runtime_seconds INTEGER NULL,
                FOREIGN KEY(library_item_id) REFERENCES library_items(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS tv_shows (
                id TEXT PRIMARY KEY,
                library_item_id TEXT NOT NULL,
                tmdb_id INTEGER NULL,
                original_name TEXT NULL,
                first_air_date TEXT NULL,
                FOREIGN KEY(library_item_id) REFERENCES library_items(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS seasons (
                id TEXT PRIMARY KEY,
                tv_show_id TEXT NOT NULL,
                season_number INTEGER NOT NULL,
                title TEXT NULL,
                poster_asset_id TEXT NULL,
                FOREIGN KEY(tv_show_id) REFERENCES tv_shows(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS episodes (
                id TEXT PRIMARY KEY,
                season_id TEXT NOT NULL,
                episode_number INTEGER NOT NULL,
                title TEXT NULL,
                overview TEXT NULL,
                still_asset_id TEXT NULL,
                air_date TEXT NULL,
                FOREIGN KEY(season_id) REFERENCES seasons(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS video_files (
                id TEXT PRIMARY KEY,
                source_id INTEGER NOT NULL,
                library_item_id TEXT NULL,
                episode_id TEXT NULL,
                relative_path TEXT NOT NULL,
                file_name TEXT NOT NULL,
                file_size_bytes INTEGER NULL,
                modified_at TEXT NULL,
                media_kind TEXT NOT NULL,
                duration_seconds REAL NOT NULL DEFAULT 0,
                container TEXT NULL,
                video_codec TEXT NULL,
                audio_codec TEXT NULL,
                subtitle_summary TEXT NULL,
                probe_json TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                missing_at TEXT NULL,
                FOREIGN KEY(source_id) REFERENCES media_sources(id) ON DELETE CASCADE,
                FOREIGN KEY(library_item_id) REFERENCES library_items(id) ON DELETE SET NULL,
                FOREIGN KEY(episode_id) REFERENCES episodes(id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS playback_progress (
                id TEXT PRIMARY KEY,
                user_id TEXT NOT NULL,
                video_file_id TEXT NOT NULL,
                position_seconds REAL NOT NULL DEFAULT 0,
                duration_seconds REAL NOT NULL DEFAULT 0,
                is_watched INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL,
                FOREIGN KEY(video_file_id) REFERENCES video_files(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS douban_metadata (
                library_item_id TEXT PRIMARY KEY,
                subject_id TEXT NOT NULL,
                subject_url TEXT NOT NULL,
                title TEXT NOT NULL,
                original_title TEXT NULL,
                year TEXT NULL,
                rating REAL NULL,
                rating_count INTEGER NULL,
                summary TEXT NULL,
                genres TEXT NULL,
                countries TEXT NULL,
                poster_url TEXT NULL,
                fetched_at TEXT NOT NULL,
                FOREIGN KEY(library_item_id) REFERENCES library_items(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS scrape_overrides (
                id TEXT PRIMARY KEY,
                target_kind TEXT NOT NULL,
                target_id TEXT NOT NULL,
                override_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS poster_assets (
                id TEXT PRIMARY KEY,
                remote_path TEXT NULL,
                local_path TEXT NOT NULL,
                width INTEGER NULL,
                height INTEGER NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS thumbnail_assets (
                id TEXT PRIMARY KEY,
                video_file_id TEXT NULL,
                local_path TEXT NOT NULL,
                width INTEGER NULL,
                height INTEGER NULL,
                created_at TEXT NOT NULL,
                FOREIGN KEY(video_file_id) REFERENCES video_files(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS transcode_jobs (
                id TEXT PRIMARY KEY,
                video_file_id TEXT NOT NULL,
                status TEXT NOT NULL,
                profile TEXT NOT NULL,
                output_directory TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                error_message TEXT NULL,
                FOREIGN KEY(video_file_id) REFERENCES video_files(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT PRIMARY KEY,
                value_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS users (
                id TEXT PRIMARY KEY,
                username TEXT NOT NULL UNIQUE,
                password_hash TEXT NOT NULL,
                role TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS api_tokens (
                id TEXT PRIMARY KEY,
                user_id TEXT NOT NULL,
                token_hash TEXT NOT NULL,
                name TEXT NULL,
                expires_at TEXT NULL,
                created_at TEXT NOT NULL,
                revoked_at TEXT NULL,
                FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_media_sources_kind ON media_sources(kind);
            CREATE INDEX IF NOT EXISTS ix_library_items_sort_title ON library_items(sort_title);
            CREATE INDEX IF NOT EXISTS ix_video_files_source_id ON video_files(source_id);
            CREATE INDEX IF NOT EXISTS ix_video_files_library_item_id ON video_files(library_item_id);
            CREATE INDEX IF NOT EXISTS ix_video_files_episode_id ON video_files(episode_id);
            CREATE INDEX IF NOT EXISTS ix_playback_progress_user_file ON playback_progress(user_id, video_file_id);
            """;
        command.ExecuteNonQuery();

        EnsureColumn("media_sources", "last_scanned_at", "TEXT NULL");
        EnsureColumn("library_items", "is_visible", "INTEGER NOT NULL DEFAULT 1");

        using var migration = connection.CreateCommand();
        migration.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (id, applied_at)
            VALUES ('0001_initial_nas_schema', $appliedAt);
            """;
        migration.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
        migration.ExecuteNonQuery();

        void EnsureColumn(string tableName, string columnName, string definition)
        {
            var exists = false;
            using var check = connection.CreateCommand();
            check.CommandText = $"PRAGMA table_info({tableName});";
            using (var reader = check.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
            }

            if (exists)
            {
                return;
            }

            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
            alter.ExecuteNonQuery();
        }
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        return connection;
    }

    public DatabaseStatus GetStatus()
    {
        var info = new FileInfo(DatabasePath);
        return new DatabaseStatus(DatabasePath, info.Exists, info.Exists ? info.Length : 0);
    }
}
