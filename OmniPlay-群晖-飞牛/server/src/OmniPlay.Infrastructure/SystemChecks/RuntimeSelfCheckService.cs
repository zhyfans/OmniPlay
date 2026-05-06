using System.Net;
using System.Net.Http.Headers;
using Microsoft.Data.Sqlite;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.FileSystem;
using OmniPlay.Infrastructure.Library;

namespace OmniPlay.Infrastructure.SystemChecks;

public sealed class RuntimeSelfCheckService : IRuntimeSelfCheckService
{
    private readonly IStoragePaths storagePaths;
    private readonly SqliteDatabase database;
    private readonly IHlsSessionService hlsSessionService;
    private readonly HttpClient httpClient;
    private readonly Uri listenUri;

    public RuntimeSelfCheckService(
        IStoragePaths storagePaths,
        SqliteDatabase database,
        IHlsSessionService hlsSessionService,
        HttpClient httpClient,
        Uri listenUri)
    {
        this.storagePaths = storagePaths;
        this.database = database;
        this.hlsSessionService = hlsSessionService;
        this.httpClient = httpClient;
        this.listenUri = listenUri;
    }

    public async Task<RuntimeSelfCheckSnapshot> CheckAsync(CancellationToken cancellationToken = default)
    {
        List<RuntimeSelfCheckItem> items = [];
        items.Add(CheckListenPort());
        items.Add(await CheckDirectoryWriteAsync("cache-write", "缓存目录写入", storagePaths.CacheDirectory, cancellationToken));
        items.Add(await CheckDirectoryWriteAsync("transcode-write", "转码目录写入", storagePaths.TranscodeDirectory, cancellationToken));
        items.Add(await CheckSqliteWriteAsync(cancellationToken));

        var capabilities = await hlsSessionService.GetCapabilitiesAsync(cancellationToken);
        items.Add(CheckFfmpeg(capabilities));
        items.Add(CheckHardwareEncoding(capabilities));
        items.Add(await CheckWebDavRangeAsync(cancellationToken));

        return new RuntimeSelfCheckSnapshot(
            ResolveAggregateStatus(items),
            DateTimeOffset.UtcNow,
            items);
    }

    private RuntimeSelfCheckItem CheckListenPort()
    {
        var host = listenUri.Host;
        var isLoopback = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                         || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
        return new RuntimeSelfCheckItem(
            "listen-port",
            "监听端口",
            isLoopback ? "warn" : "ok",
            isLoopback
                ? $"当前监听 {listenUri}，局域网设备可能无法直接访问。"
                : $"当前监听 {listenUri}，可用于 NAS 局域网入口。",
            new Dictionary<string, string>
            {
                ["host"] = host,
                ["port"] = listenUri.Port.ToString()
            });
    }

    private static async Task<RuntimeSelfCheckItem> CheckDirectoryWriteAsync(
        string key,
        string label,
        string directory,
        CancellationToken cancellationToken)
    {
        var probePath = Path.Combine(directory, $".omniplay-self-check-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(probePath, DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
            File.Delete(probePath);
            return new RuntimeSelfCheckItem(
                key,
                label,
                "ok",
                $"可写：{directory}",
                new Dictionary<string, string> { ["path"] = directory });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(probePath);
            return new RuntimeSelfCheckItem(
                key,
                label,
                "error",
                $"无法写入 {directory}：{ex.Message}",
                new Dictionary<string, string> { ["path"] = directory });
        }
    }

    private async Task<RuntimeSelfCheckItem> CheckSqliteWriteAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SAVEPOINT runtime_self_check;
                CREATE TABLE IF NOT EXISTS runtime_self_check_probe (
                    id TEXT PRIMARY KEY,
                    checked_at TEXT NOT NULL
                );
                INSERT OR REPLACE INTO runtime_self_check_probe (id, checked_at)
                VALUES ('probe', $checkedAt);
                ROLLBACK TO runtime_self_check;
                RELEASE runtime_self_check;
                """;
            command.Parameters.AddWithValue("$checkedAt", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new RuntimeSelfCheckItem(
                "sqlite-write",
                "SQLite 写入",
                "ok",
                $"数据库可写：{database.DatabasePath}",
                new Dictionary<string, string> { ["path"] = database.DatabasePath });
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return new RuntimeSelfCheckItem(
                "sqlite-write",
                "SQLite 写入",
                "error",
                $"数据库写入失败：{ex.Message}",
                new Dictionary<string, string> { ["path"] = database.DatabasePath });
        }
    }

    private static RuntimeSelfCheckItem CheckFfmpeg(FfmpegTranscodeCapabilities capabilities)
    {
        return new RuntimeSelfCheckItem(
            "ffmpeg",
            "FFmpeg",
            capabilities.IsAvailable ? "ok" : "error",
            capabilities.IsAvailable
                ? $"FFmpeg 可用：{capabilities.FfmpegPath}"
                : $"FFmpeg 不可用：{capabilities.ErrorMessage ?? capabilities.FfmpegPath}",
            new Dictionary<string, string> { ["path"] = capabilities.FfmpegPath });
    }

    private static RuntimeSelfCheckItem CheckHardwareEncoding(FfmpegTranscodeCapabilities capabilities)
    {
        if (!capabilities.IsAvailable)
        {
            return new RuntimeSelfCheckItem(
                "hardware-encoding",
                "硬件编码",
                "warn",
                "FFmpeg 不可用，暂不能检测硬件 H.264 编码器。");
        }

        return new RuntimeSelfCheckItem(
            "hardware-encoding",
            "硬件编码",
            capabilities.HardwareEncoders.Count > 0 ? "ok" : "warn",
            capabilities.HardwareEncoders.Count > 0
                ? $"发现硬件编码器：{string.Join(", ", capabilities.HardwareEncoders)}"
                : "未发现可用硬件 H.264 编码器，将使用软件转码。",
            new Dictionary<string, string>
            {
                ["preferred"] = capabilities.PreferredHardwareEncoder ?? "",
                ["encoders"] = string.Join(",", capabilities.HardwareEncoders)
            });
    }

    private async Task<RuntimeSelfCheckItem> CheckWebDavRangeAsync(CancellationToken cancellationToken)
    {
        var row = await ReadFirstWebDavVideoAsync(cancellationToken);
        if (row is null)
        {
            return new RuntimeSelfCheckItem(
                "webdav-range",
                "WebDAV Range",
                "warn",
                "尚未发现可检测的 WebDAV 视频文件。");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                WebDavPlaybackCacheService.BuildWebDavFileUri(row.SourceBaseUrl, row.RelativePath));
            request.Headers.Range = new RangeHeaderValue(0, 0);
            WebDavPlaybackCacheService.ApplyBasicAuth(
                request,
                row.Username,
                WebDavPlaybackCacheService.ReadPassword(row.SecretJson));
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            return response.StatusCode switch
            {
                HttpStatusCode.PartialContent => new RuntimeSelfCheckItem(
                    "webdav-range",
                    "WebDAV Range",
                    "ok",
                    $"远端支持 Range：{row.FileName}",
                    new Dictionary<string, string> { ["fileId"] = row.Id }),
                HttpStatusCode.OK => new RuntimeSelfCheckItem(
                    "webdav-range",
                    "WebDAV Range",
                    "warn",
                    $"远端忽略 Range 请求，直出可能回退为代理整流：{row.FileName}",
                    new Dictionary<string, string> { ["fileId"] = row.Id }),
                HttpStatusCode.Unauthorized => new RuntimeSelfCheckItem(
                    "webdav-range",
                    "WebDAV Range",
                    "error",
                    $"WebDAV 认证失败：{row.FileName}",
                    new Dictionary<string, string> { ["fileId"] = row.Id }),
                _ => new RuntimeSelfCheckItem(
                    "webdav-range",
                    "WebDAV Range",
                    "warn",
                    $"远端 Range 检测返回 HTTP {(int)response.StatusCode}：{row.FileName}",
                    new Dictionary<string, string> { ["fileId"] = row.Id })
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RuntimeSelfCheckItem(
                "webdav-range",
                "WebDAV Range",
                "warn",
                $"WebDAV Range 检测超时：{row.FileName}",
                new Dictionary<string, string> { ["fileId"] = row.Id });
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new RuntimeSelfCheckItem(
                "webdav-range",
                "WebDAV Range",
                "warn",
                $"WebDAV Range 检测失败：{ex.Message}",
                new Dictionary<string, string> { ["fileId"] = row.Id });
        }
    }

    private async Task<WebDavProbeRow?> ReadFirstWebDavVideoAsync(CancellationToken cancellationToken)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT vf.id,
                   vf.file_name,
                   vf.relative_path,
                   ms.base_url,
                   c.username,
                   c.secret_json
            FROM video_files vf
            JOIN media_sources ms ON ms.id = vf.source_id
            LEFT JOIN media_source_credentials c ON c.id = ms.auth_reference
            WHERE ms.kind = 'webdav'
              AND ms.is_enabled = 1
              AND ms.removed_at IS NULL
              AND vf.missing_at IS NULL
            ORDER BY vf.updated_at DESC
            LIMIT 1;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WebDavProbeRow(
            reader.GetString(0),
            reader.GetString(1),
            MediaNameParser.NormalizeRelativePath(reader.GetString(2)),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private static string ResolveAggregateStatus(IReadOnlyList<RuntimeSelfCheckItem> items)
    {
        if (items.Any(static item => string.Equals(item.Status, "error", StringComparison.OrdinalIgnoreCase)))
        {
            return "error";
        }

        return items.Any(static item => string.Equals(item.Status, "warn", StringComparison.OrdinalIgnoreCase))
            ? "warn"
            : "ok";
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

    private sealed record WebDavProbeRow(
        string Id,
        string FileName,
        string RelativePath,
        string SourceBaseUrl,
        string? Username,
        string? SecretJson);
}
