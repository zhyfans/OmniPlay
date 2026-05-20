using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.Data;

public sealed class MediaSourceRepository : IMediaSourceRepository
{
    private readonly SqliteDatabase database;

    public MediaSourceRepository(SqliteDatabase database)
    {
        this.database = database;
    }

    public async Task<IReadOnlyList<MediaSourceSummary>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, kind, base_url, is_enabled, created_at, updated_at, last_scanned_at
            FROM media_sources
            WHERE removed_at IS NULL
            ORDER BY id ASC;
            """;

        List<MediaSourceSummary> sources = [];
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sources.Add(ReadSource(reader));
        }

        return sources;
    }

    public async Task<MediaSourceSummary> AddLocalAsync(
        string name,
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("本地目录不能为空。", nameof(path));
        }

        var normalizedPath = Path.GetFullPath(path.Trim());
        if (!Directory.Exists(normalizedPath))
        {
            throw new DirectoryNotFoundException($"本地目录不存在：{normalizedPath}");
        }

        var displayName = string.IsNullOrWhiteSpace(name)
            ? Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : name.Trim();

        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = normalizedPath;
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = database.OpenConnection();

        var existingId = await FindExistingSourceIdAsync(connection, "local", normalizedPath, cancellationToken);
        if (existingId.HasValue)
        {
            using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE media_sources
                SET name = $name,
                    is_enabled = 1,
                    disabled_at = NULL,
                    removed_at = NULL,
                    updated_at = $updatedAt
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$name", displayName);
            update.Parameters.AddWithValue("$updatedAt", now);
            update.Parameters.AddWithValue("$id", existingId.Value);
            await update.ExecuteNonQueryAsync(cancellationToken);

            return await GetByIdAsync(connection, existingId.Value, cancellationToken)
                   ?? throw new InvalidOperationException("媒体源更新后无法读取。");
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO media_sources (name, kind, base_url, auth_reference, is_enabled, created_at, updated_at)
            VALUES ($name, 'local', $baseUrl, NULL, 1, $createdAt, $updatedAt);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$name", displayName);
        insert.Parameters.AddWithValue("$baseUrl", normalizedPath);
        insert.Parameters.AddWithValue("$createdAt", now);
        insert.Parameters.AddWithValue("$updatedAt", now);

        var id = (long)(await insert.ExecuteScalarAsync(cancellationToken)
                        ?? throw new InvalidOperationException("新增媒体源失败。"));

        return await GetByIdAsync(connection, id, cancellationToken)
               ?? throw new InvalidOperationException("媒体源新增后无法读取。");
    }

    public async Task<MediaSourceSummary> AddWebDavAsync(
        string name,
        string url,
        string? username,
        string? password,
        CancellationToken cancellationToken = default)
    {
        var normalizedUrl = NormalizeWebDavUrl(url);
        var normalizedUri = new Uri(normalizedUrl, UriKind.Absolute);
        var displayName = string.IsNullOrWhiteSpace(name)
            ? BuildWebDavDisplayName(normalizedUri)
            : name.Trim();
        var normalizedUsername = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        var hasCredential = normalizedUsername is not null || !string.IsNullOrEmpty(password);
        var credentialId = hasCredential ? Guid.NewGuid().ToString("N") : null;
        var now = DateTimeOffset.UtcNow.ToString("O");

        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var existingId = await FindExistingSourceIdAsync(
            connection,
            "webdav",
            normalizedUrl,
            cancellationToken,
            transaction);
        var sourceId = existingId ?? await InsertWebDavSourceAsync(
            connection,
            transaction,
            displayName,
            normalizedUrl,
            credentialId,
            now,
            cancellationToken);

        if (existingId.HasValue)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE media_sources
                SET name = $name,
                    base_url = $baseUrl,
                    auth_reference = $authReference,
                    is_enabled = 1,
                    disabled_at = NULL,
                    removed_at = NULL,
                    updated_at = $updatedAt
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$name", displayName);
            update.Parameters.AddWithValue("$baseUrl", normalizedUrl);
            update.Parameters.AddWithValue("$authReference", (object?)credentialId ?? DBNull.Value);
            update.Parameters.AddWithValue("$updatedAt", now);
            update.Parameters.AddWithValue("$id", sourceId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await ReplaceWebDavCredentialAsync(
            connection,
            transaction,
            sourceId,
            credentialId,
            normalizedUsername,
            password,
            now,
            cancellationToken);
        transaction.Commit();

        return await GetByIdAsync(connection, sourceId, cancellationToken)
               ?? throw new InvalidOperationException("WebDAV 媒体源保存后无法读取。");
    }

    public async Task<MediaSourceSummary?> UpdateAsync(
        long id,
        UpdateMediaSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        var hasName = request.Name is not null;
        var hasEnabled = request.IsEnabled.HasValue;
        if (!hasName && !hasEnabled)
        {
            using var readConnection = database.OpenConnection();
            return await GetByIdAsync(readConnection, id, cancellationToken);
        }

        var displayName = request.Name?.Trim();
        if (hasName && string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("媒体源名称不能为空。", nameof(request));
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = database.OpenConnection();
        using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE media_sources
            SET name = CASE WHEN $hasName = 1 THEN $name ELSE name END,
                is_enabled = CASE WHEN $hasEnabled = 1 THEN $isEnabled ELSE is_enabled END,
                disabled_at = CASE
                    WHEN $hasEnabled = 1 AND $isEnabled = 0 THEN COALESCE(disabled_at, $updatedAt)
                    WHEN $hasEnabled = 1 AND $isEnabled = 1 THEN NULL
                    ELSE disabled_at
                END,
                updated_at = $updatedAt
            WHERE id = $id
              AND removed_at IS NULL;
            """;
        update.Parameters.AddWithValue("$hasName", hasName ? 1 : 0);
        update.Parameters.AddWithValue("$name", displayName ?? string.Empty);
        update.Parameters.AddWithValue("$hasEnabled", hasEnabled ? 1 : 0);
        update.Parameters.AddWithValue("$isEnabled", request.IsEnabled == true ? 1 : 0);
        update.Parameters.AddWithValue("$updatedAt", now);
        update.Parameters.AddWithValue("$id", id);

        var affected = await update.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
        {
            return null;
        }

        return await GetByIdAsync(connection, id, cancellationToken)
               ?? throw new InvalidOperationException("媒体源更新后无法读取。");
    }

    public async Task<bool> RemoveAsync(long id, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = database.OpenConnection();
        using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE media_sources
            SET is_enabled = 0,
                disabled_at = COALESCE(disabled_at, $updatedAt),
                removed_at = COALESCE(removed_at, $updatedAt),
                updated_at = $updatedAt
            WHERE id = $id
            ;
            """;
        update.Parameters.AddWithValue("$updatedAt", now);
        update.Parameters.AddWithValue("$id", id);

        return await update.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static async Task<long?> FindExistingSourceIdAsync(
        SqliteConnection connection,
        string kind,
        string baseUrl,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id
            FROM media_sources
            WHERE kind = $kind AND base_url = $baseUrl
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$baseUrl", baseUrl);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    private static async Task<long> InsertWebDavSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string displayName,
        string normalizedUrl,
        string? credentialId,
        string now,
        CancellationToken cancellationToken)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO media_sources (name, kind, base_url, auth_reference, is_enabled, created_at, updated_at)
            VALUES ($name, 'webdav', $baseUrl, $authReference, 1, $createdAt, $updatedAt);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$name", displayName);
        insert.Parameters.AddWithValue("$baseUrl", normalizedUrl);
        insert.Parameters.AddWithValue("$authReference", (object?)credentialId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$createdAt", now);
        insert.Parameters.AddWithValue("$updatedAt", now);

        return (long)(await insert.ExecuteScalarAsync(cancellationToken)
                      ?? throw new InvalidOperationException("新增 WebDAV 媒体源失败。"));
    }

    private static async Task ReplaceWebDavCredentialAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sourceId,
        string? credentialId,
        string? username,
        string? password,
        string now,
        CancellationToken cancellationToken)
    {
        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            DELETE FROM media_source_credentials
            WHERE source_id = $sourceId;
            """;
        delete.Parameters.AddWithValue("$sourceId", sourceId);
        await delete.ExecuteNonQueryAsync(cancellationToken);

        if (credentialId is null)
        {
            return;
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO media_source_credentials (id, source_id, kind, username, secret_json, created_at, updated_at)
            VALUES ($id, $sourceId, 'webdav-basic', $username, $secretJson, $createdAt, $updatedAt);
            """;
        insert.Parameters.AddWithValue("$id", credentialId);
        insert.Parameters.AddWithValue("$sourceId", sourceId);
        insert.Parameters.AddWithValue("$username", (object?)username ?? DBNull.Value);
        insert.Parameters.AddWithValue(
            "$secretJson",
            JsonSerializer.Serialize(
                new WebDavCredentialSecret(password ?? string.Empty),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        insert.Parameters.AddWithValue("$createdAt", now);
        insert.Parameters.AddWithValue("$updatedAt", now);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeWebDavUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("WebDAV 地址不能为空。", nameof(url));
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("WebDAV 地址格式不正确。", nameof(url));
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("WebDAV 地址只支持 http 或 https。", nameof(url));
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty,
            Query = string.Empty,
            UserName = string.Empty,
            Password = string.Empty
        };

        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static string BuildWebDavDisplayName(Uri uri)
    {
        var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        var path = uri.AbsolutePath.Trim('/');
        return string.IsNullOrWhiteSpace(path) ? host : $"{host}/{path}";
    }

    private static async Task<MediaSourceSummary?> GetByIdAsync(
        SqliteConnection connection,
        long id,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, kind, base_url, is_enabled, created_at, updated_at, last_scanned_at
            FROM media_sources
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSource(reader) : null;
    }

    private static MediaSourceSummary ReadSource(SqliteDataReader reader)
    {
        return new MediaSourceSummary(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4) == 1,
            DateTimeOffset.Parse(reader.GetString(5)),
            DateTimeOffset.Parse(reader.GetString(6)),
            reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)));
    }

    private sealed record WebDavCredentialSecret(string Password);
}
