using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.Library;

namespace OmniPlay.Infrastructure.FileSystem;

public sealed class PlayableFileResolver : IPlayableFileResolver
{
    private readonly SqliteDatabase database;
    private readonly IStoragePaths storagePaths;
    private readonly HttpClient httpClient;

    public PlayableFileResolver(
        SqliteDatabase database,
        IStoragePaths storagePaths,
        HttpClient httpClient)
    {
        this.database = database;
        this.storagePaths = storagePaths;
        this.httpClient = httpClient;
    }

    public async Task<PlayableVideoFile?> ResolveAsync(
        string videoFileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoFileId))
        {
            return null;
        }

        using var connection = database.OpenConnection();
        var row = await ReadPlaybackRowAsync(connection, videoFileId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        return row.SourceKind.ToLowerInvariant() switch
        {
            "local" => ResolveLocalFile(row),
            "webdav" => await ResolveWebDavFileAsync(row, cancellationToken),
            _ => null
        };
    }

    private static async Task<PlaybackFileRow?> ReadPlaybackRowAsync(
        SqliteConnection connection,
        string videoFileId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT vf.id,
                   vf.file_name,
                   vf.media_kind,
                   vf.file_size_bytes,
                   vf.duration_seconds,
                   vf.relative_path,
                   vf.container,
                   vf.video_codec,
                   vf.audio_codec,
                   vf.subtitle_summary,
                   ms.kind,
                   ms.base_url,
                   c.username,
                   c.secret_json
            FROM video_files vf
            JOIN media_sources ms ON ms.id = vf.source_id
            LEFT JOIN media_source_credentials c ON c.id = ms.auth_reference
            WHERE vf.id = $id
              AND vf.missing_at IS NULL
              AND ms.is_enabled = 1
              AND ms.removed_at IS NULL
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", videoFileId.Trim());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PlaybackFileRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.GetDouble(4),
            MediaNameParser.NormalizeRelativePath(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13));
    }

    private static PlayableVideoFile? ResolveLocalFile(PlaybackFileRow row)
    {
        var sourceRoot = Path.GetFullPath(row.SourceBaseUrl);
        var relativePath = row.RelativePath.Replace('/', Path.DirectorySeparatorChar);
        var absolutePath = Path.GetFullPath(Path.Combine(sourceRoot, relativePath));
        if (!IsPathInsideRoot(sourceRoot, absolutePath) || !File.Exists(absolutePath))
        {
            return null;
        }

        return CreatePlayable(row, absolutePath);
    }

    private async Task<PlayableVideoFile?> ResolveWebDavFileAsync(
        PlaybackFileRow row,
        CancellationToken cancellationToken)
    {
        var cachePath = ResolveWebDavCachePath(row);
        if (IsCacheUsable(cachePath, row.FileSizeBytes))
        {
            TouchLastAccessTime(cachePath);
            return CreatePlayable(row, cachePath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var remoteUri = BuildWebDavFileUri(row.SourceBaseUrl, row.RelativePath);
        var tempPath = $"{cachePath}.{Guid.NewGuid():N}.part";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, remoteUri);
            ApplyBasicAuth(request, row.Username, ReadPassword(row.SecretJson));
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using (var remote = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var local = File.Create(tempPath))
            {
                await remote.CopyToAsync(local, cancellationToken);
            }

            File.Move(tempPath, cachePath, overwrite: true);
            TouchLastAccessTime(cachePath);
            return CreatePlayable(row, cachePath);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private string ResolveWebDavCachePath(PlaybackFileRow row)
    {
        var key = string.Join('|', row.Id, row.RelativePath, row.FileSizeBytes?.ToString() ?? "", row.SourceBaseUrl);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        var extension = Path.GetExtension(row.FileName);
        if (extension.Length > 16 || extension.Contains(Path.DirectorySeparatorChar) || extension.Contains(Path.AltDirectorySeparatorChar))
        {
            extension = ".bin";
        }

        return Path.Combine(storagePaths.CacheDirectory, "webdav", $"{hash}{extension}");
    }

    private static bool IsCacheUsable(string cachePath, long? expectedSize)
    {
        if (!File.Exists(cachePath))
        {
            return false;
        }

        if (!expectedSize.HasValue)
        {
            return new FileInfo(cachePath).Length > 0;
        }

        return new FileInfo(cachePath).Length == expectedSize.Value;
    }

    private static void TouchLastAccessTime(string cachePath)
    {
        try
        {
            File.SetLastAccessTimeUtc(cachePath, DateTime.UtcNow);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static Uri BuildWebDavFileUri(string sourceBaseUrl, string relativePath)
    {
        var baseUrl = sourceBaseUrl.TrimEnd('/') + "/";
        var escapedRelativePath = string.Join(
            '/',
            MediaNameParser.NormalizeRelativePath(relativePath)
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
        return new Uri(new Uri(baseUrl, UriKind.Absolute), escapedRelativePath);
    }

    private static void ApplyBasicAuth(HttpRequestMessage request, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) && string.IsNullOrEmpty(password))
        {
            return;
        }

        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{username?.Trim() ?? string.Empty}:{password ?? string.Empty}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    }

    private static string? ReadPassword(string? secretJson)
    {
        if (string.IsNullOrWhiteSpace(secretJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(secretJson);
        if (document.RootElement.TryGetProperty("password", out var password))
        {
            return password.GetString();
        }

        if (document.RootElement.TryGetProperty("Password", out var legacyPassword))
        {
            return legacyPassword.GetString();
        }

        return null;
    }

    private static PlayableVideoFile CreatePlayable(PlaybackFileRow row, string absolutePath)
    {
        return new PlayableVideoFile(
            row.Id,
            absolutePath,
            row.FileName,
            row.MediaKind,
            row.FileSizeBytes,
            row.DurationSeconds,
            row.Container,
            row.VideoCodec,
            row.AudioCodec,
            row.SubtitleSummary);
    }

    private static bool IsPathInsideRoot(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PlaybackFileRow(
        string Id,
        string FileName,
        string MediaKind,
        long? FileSizeBytes,
        double DurationSeconds,
        string RelativePath,
        string? Container,
        string? VideoCodec,
        string? AudioCodec,
        string? SubtitleSummary,
        string SourceKind,
        string SourceBaseUrl,
        string? Username,
        string? SecretJson);
}
