using System.Collections.Concurrent;
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

public sealed class WebDavPlaybackCacheService : IPlaybackCacheService
{
    private readonly ConcurrentDictionary<string, DownloadState> downloads = new(StringComparer.Ordinal);
    private readonly SqliteDatabase database;
    private readonly IStoragePaths storagePaths;
    private readonly HttpClient httpClient;

    public WebDavPlaybackCacheService(
        SqliteDatabase database,
        IStoragePaths storagePaths,
        HttpClient httpClient)
    {
        this.database = database;
        this.storagePaths = storagePaths;
        this.httpClient = httpClient;
    }

    public async Task<PlaybackCacheStatus?> GetStatusAsync(
        string videoFileId,
        CancellationToken cancellationToken = default)
    {
        var row = await ReadRowAsync(videoFileId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        if (!IsWebDav(row))
        {
            return LocalReady(row.Id);
        }

        var cachePath = ResolveWebDavCachePath(row);
        if (IsCacheUsable(cachePath, row.FileSizeBytes))
        {
            TouchLastAccessTime(cachePath);
            downloads.TryRemove(row.Id, out _);
            return Ready(row);
        }

        return downloads.TryGetValue(row.Id, out var state)
            ? state.ToStatus(row)
            : Pending(row);
    }

    public async Task<PlaybackCacheStatus?> StartAsync(
        string videoFileId,
        CancellationToken cancellationToken = default)
    {
        var row = await ReadRowAsync(videoFileId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        if (!IsWebDav(row))
        {
            return LocalReady(row.Id);
        }

        var cachePath = ResolveWebDavCachePath(row);
        if (IsCacheUsable(cachePath, row.FileSizeBytes))
        {
            TouchLastAccessTime(cachePath);
            downloads.TryRemove(row.Id, out _);
            return Ready(row);
        }

        if (downloads.TryGetValue(row.Id, out var currentState) && !currentState.IsActive)
        {
            downloads.TryRemove(row.Id, out _);
        }

        var state = downloads.GetOrAdd(row.Id, _ =>
        {
            var nextState = new DownloadState(row.FileSizeBytes);
            nextState.Task = Task.Run(() => DownloadAsync(row, cachePath, nextState), CancellationToken.None);
            return nextState;
        });
        return state.ToStatus(row);
    }

    public async Task<PlaybackCacheStatus?> CancelAsync(
        string videoFileId,
        CancellationToken cancellationToken = default)
    {
        var row = await ReadRowAsync(videoFileId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        if (!IsWebDav(row))
        {
            return LocalReady(row.Id);
        }

        if (downloads.TryGetValue(row.Id, out var state))
        {
            state.Cancel();
            return state.ToStatus(row);
        }

        return await GetStatusAsync(videoFileId, cancellationToken);
    }

    public async Task<string?> EnsureCachedAsync(
        string videoFileId,
        CancellationToken cancellationToken = default)
    {
        var row = await ReadRowAsync(videoFileId, cancellationToken);
        if (row is null || !IsWebDav(row))
        {
            return null;
        }

        var cachePath = ResolveWebDavCachePath(row);
        if (IsCacheUsable(cachePath, row.FileSizeBytes))
        {
            TouchLastAccessTime(cachePath);
            return cachePath;
        }

        var state = downloads.GetOrAdd(row.Id, _ =>
        {
            var nextState = new DownloadState(row.FileSizeBytes);
            nextState.Task = Task.Run(() => DownloadAsync(row, cachePath, nextState), CancellationToken.None);
            return nextState;
        });

        try
        {
            await state.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        return IsCacheUsable(cachePath, row.FileSizeBytes) ? cachePath : null;
    }

    private async Task DownloadAsync(
        PlaybackCacheRow row,
        string cachePath,
        DownloadState state)
    {
        var tempPath = $"{cachePath}.{Guid.NewGuid():N}.part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildWebDavFileUri(row.SourceBaseUrl, row.RelativePath));
            ApplyBasicAuth(request, row.Username, ReadPassword(row.SecretJson));
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                state.CancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                state.Fail($"WebDAV 下载失败：HTTP {(int)response.StatusCode}。");
                return;
            }

            state.SetTotal(response.Content.Headers.ContentLength ?? row.FileSizeBytes);
            await using var remote = await response.Content.ReadAsStreamAsync(state.CancellationToken);
            await using var local = File.Create(tempPath);
            var buffer = new byte[1024 * 256];
            while (true)
            {
                var read = await remote.ReadAsync(buffer.AsMemory(0, buffer.Length), state.CancellationToken);
                if (read == 0)
                {
                    break;
                }

                await local.WriteAsync(buffer.AsMemory(0, read), state.CancellationToken);
                state.AddDownloaded(read);
            }

            File.Move(tempPath, cachePath, overwrite: true);
            TouchLastAccessTime(cachePath);
            state.Complete();
        }
        catch (OperationCanceledException) when (state.CancellationToken.IsCancellationRequested)
        {
            state.MarkCanceled();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            state.Fail(ex.Message);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private async Task<PlaybackCacheRow?> ReadRowAsync(
        string videoFileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(videoFileId))
        {
            return null;
        }

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT vf.id,
                   vf.file_name,
                   vf.file_size_bytes,
                   vf.relative_path,
                   vf.video_codec,
                   vf.audio_codec,
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

        return new PlaybackCacheRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            MediaNameParser.NormalizeRelativePath(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));
    }

    private string ResolveWebDavCachePath(PlaybackCacheRow row)
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

    public static Uri BuildWebDavFileUri(string sourceBaseUrl, string relativePath)
    {
        var baseUrl = sourceBaseUrl.TrimEnd('/') + "/";
        var escapedRelativePath = string.Join(
            '/',
            MediaNameParser.NormalizeRelativePath(relativePath)
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
        return new Uri(new Uri(baseUrl, UriKind.Absolute), escapedRelativePath);
    }

    public static string? ReadPassword(string? secretJson)
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

    public static void ApplyBasicAuth(HttpRequestMessage request, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) && string.IsNullOrEmpty(password))
        {
            return;
        }

        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{username?.Trim() ?? string.Empty}:{password ?? string.Empty}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    }

    public static bool IsCacheUsable(string cachePath, long? expectedSize)
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

    public static void TouchLastAccessTime(string cachePath)
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

    private static bool IsWebDav(PlaybackCacheRow row)
    {
        return string.Equals(row.SourceKind, "webdav", StringComparison.OrdinalIgnoreCase);
    }

    private static PlaybackCacheStatus LocalReady(string videoFileId)
    {
        return new PlaybackCacheStatus(videoFileId, false, true, false, false, null, 0, 100, "ready", null);
    }

    private static PlaybackCacheStatus Ready(PlaybackCacheRow row)
    {
        return new PlaybackCacheStatus(
            row.Id,
            true,
            true,
            false,
            false,
            row.FileSizeBytes,
            row.FileSizeBytes ?? 0,
            100,
            "ready",
            null,
            CanUseRangeProxy(row));
    }

    private static PlaybackCacheStatus Pending(PlaybackCacheRow row)
    {
        return new PlaybackCacheStatus(
            row.Id,
            true,
            false,
            false,
            false,
            row.FileSizeBytes,
            0,
            0,
            "pending",
            null,
            CanUseRangeProxy(row));
    }

    private static bool CanUseRangeProxy(PlaybackCacheRow row)
    {
        var extension = Path.GetExtension(row.FileName).ToLowerInvariant();
        var videoCodec = row.VideoCodec?.Trim().ToLowerInvariant();
        var audioCodec = row.AudioCodec?.Trim().ToLowerInvariant();
        return extension switch
        {
            ".mp4" or ".m4v" or ".mov" when string.IsNullOrWhiteSpace(videoCodec)
                                             || videoCodec is "h264" or "avc1" =>
                string.IsNullOrWhiteSpace(audioCodec) || audioCodec is "aac" or "mp3",
            ".webm" when string.IsNullOrWhiteSpace(videoCodec)
                         || videoCodec is "vp8" or "vp9" or "av1" =>
                string.IsNullOrWhiteSpace(audioCodec) || audioCodec is "opus" or "vorbis",
            _ => false
        };
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

    private sealed record PlaybackCacheRow(
        string Id,
        string FileName,
        long? FileSizeBytes,
        string RelativePath,
        string? VideoCodec,
        string? AudioCodec,
        string SourceKind,
        string SourceBaseUrl,
        string? Username,
        string? SecretJson);

    private sealed class DownloadState
    {
        private readonly CancellationTokenSource cancellationTokenSource = new();
        private readonly object syncRoot = new();
        private long downloadedBytes;
        private bool isCompleted;
        private bool isCanceled;
        private string? errorMessage;

        public DownloadState(long? totalBytes)
        {
            TotalBytes = totalBytes;
        }

        public long? TotalBytes { get; private set; }

        public Task Task { get; set; } = Task.CompletedTask;

        public CancellationToken CancellationToken => cancellationTokenSource.Token;

        public bool IsActive
        {
            get
            {
                lock (syncRoot)
                {
                    return !isCompleted && !isCanceled && errorMessage is null;
                }
            }
        }

        public void SetTotal(long? value)
        {
            if (value.HasValue)
            {
                TotalBytes = value;
            }
        }

        public void AddDownloaded(long value)
        {
            Interlocked.Add(ref downloadedBytes, value);
        }

        public void Complete()
        {
            lock (syncRoot)
            {
                isCompleted = true;
                errorMessage = null;
            }
        }

        public void MarkCanceled()
        {
            lock (syncRoot)
            {
                isCanceled = true;
                errorMessage = "缓存已取消。";
            }
        }

        public void Fail(string message)
        {
            lock (syncRoot)
            {
                errorMessage = message;
            }
        }

        public void Cancel()
        {
            cancellationTokenSource.Cancel();
        }

        public PlaybackCacheStatus ToStatus(PlaybackCacheRow row)
        {
            lock (syncRoot)
            {
                var downloaded = Volatile.Read(ref downloadedBytes);
                var isRunning = !isCompleted && !isCanceled && errorMessage is null;
                var percent = TotalBytes is > 0
                    ? Math.Round(Math.Clamp((double)downloaded / TotalBytes.Value * 100, 0, 99.9), 1)
                    : (double?)null;
                var state = errorMessage is not null
                    ? isCanceled ? "canceled" : "failed"
                    : isCompleted ? "ready" : "downloading";

                return new PlaybackCacheStatus(
                    row.Id,
                    true,
                    isCompleted,
                    isRunning,
                    isRunning,
                    TotalBytes,
                    downloaded,
                    isCompleted ? 100 : percent,
                    state,
                    errorMessage,
                    CanUseRangeProxy(row));
            }
        }
    }
}
