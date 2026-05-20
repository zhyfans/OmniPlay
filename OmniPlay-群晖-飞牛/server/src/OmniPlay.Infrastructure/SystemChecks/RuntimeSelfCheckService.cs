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
        items.Add(CheckHardwareMediaAcceleration(capabilities));
        if (CheckHardwareDeviceAccess(capabilities) is { } hardwareDeviceCheck)
        {
            items.Add(hardwareDeviceCheck);
        }

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
        var isSystemFfmpeg = string.Equals(capabilities.FfmpegPath, "/usr/bin/ffmpeg", StringComparison.Ordinal)
                             || string.Equals(capabilities.FfmpegPath, "ffmpeg", StringComparison.Ordinal);
        var availableDetail = isSystemFfmpeg
            ? $"FFmpeg 可用：{capabilities.FfmpegPath}。当前可能是 DSM 系统精简版；如硬解能力不完整，请安装 ffmpeg7 或在 media-tools.env 指定完整 FFmpeg。"
            : $"FFmpeg 可用：{capabilities.FfmpegPath}";
        return new RuntimeSelfCheckItem(
            "ffmpeg",
            "FFmpeg",
            capabilities.IsAvailable ? "ok" : "error",
            capabilities.IsAvailable
                ? availableDetail
                : $"FFmpeg 不可用：{capabilities.ErrorMessage ?? capabilities.FfmpegPath}",
            new Dictionary<string, string> { ["path"] = capabilities.FfmpegPath });
    }

    private static RuntimeSelfCheckItem CheckHardwareMediaAcceleration(FfmpegTranscodeCapabilities capabilities)
    {
        if (!capabilities.IsAvailable)
        {
            return new RuntimeSelfCheckItem(
                "hardware-encoding",
                "硬件解码/编码",
                "warn",
                "FFmpeg 不可用，暂不能检测硬件解码/编码能力。");
        }

        var hasEncoder = capabilities.HardwareEncoders.Count > 0;
        var hasDecoder = capabilities.HardwareDecoders.Count > 0;
        var hasAccelerator = capabilities.HardwareAccelerators.Count > 0;
        var hasPreferredEncoder = !string.IsNullOrWhiteSpace(capabilities.PreferredHardwareEncoder);
        var detectedEncoders = capabilities.DetectedHardwareEncoders ?? capabilities.HardwareEncoders;
        var detectedDecoders = capabilities.DetectedHardwareDecoders ?? capabilities.HardwareDecoders;
        var detectedAccelerators = capabilities.DetectedHardwareAccelerators ?? capabilities.HardwareAccelerators;
        var relevantEncoders = ResolveRelevantHardwareEncoders(capabilities, detectedEncoders);
        var hasDetectedEncoder = relevantEncoders.Count > 0;
        var missingCommonDecoders = MissingCommonHardwareDecoders(capabilities);
        var status = hasPreferredEncoder && (hasDecoder || hasAccelerator) && missingCommonDecoders.Count == 0
            ? "ok"
            : "warn";
        var details = new List<string>();
        if (hasPreferredEncoder)
        {
            details.Add($"HLS 硬件输出编码器：{capabilities.PreferredHardwareEncoder}{DescribeEncoderCompatibility(capabilities.PreferredHardwareEncoder)}");
        }
        else if (hasDetectedEncoder)
        {
            details.Add($"当前 NAS 可尝试的硬件路径：{FormatHardwareKinds(relevantEncoders)}，但没有通过实机初始化的硬件编码器，已暂不启用硬件转码");
            var probeSummary = FormatHardwareProbeErrors(capabilities.HardwareEncoderProbeErrors);
            if (!string.IsNullOrWhiteSpace(probeSummary))
            {
                details.Add(probeSummary);
            }
        }
        else
        {
            details.Add("未检测到 FFmpeg 暴露的硬件编码器，视频转码会不可用");
        }

        if (!hasPreferredEncoder && hasDetectedEncoder)
        {
            details.Add("需要转码的视频会被阻止软解/软转码；Range 直出和 HLS 转封装不受影响");
        }

        if (hasAccelerator)
        {
            details.Add($"加速器：{string.Join(", ", capabilities.HardwareAccelerators)}");
        }

        if (hasDecoder)
        {
            details.Add($"解码器：{string.Join(", ", capabilities.HardwareDecoders)}");
        }

        if (hasEncoder)
        {
            details.Add($"编码器：{string.Join(", ", capabilities.HardwareEncoders)}");
        }

        if (missingCommonDecoders.Count > 0)
        {
            if (!hasDetectedEncoder || hasPreferredEncoder)
            {
                details.Add($"未检测到这些常见编码的硬件解码路径：{string.Join(", ", missingCommonDecoders)}，对应视频可软件解码后再交给硬件编码器输出");
            }
        }

        return new RuntimeSelfCheckItem(
            "hardware-encoding",
            "硬件解码/编码",
            status,
            details.Count > 0
                ? string.Join("；", details)
                : "未发现 FFmpeg 暴露的硬件解码/编码能力。需要转码的视频必须先修复 FFmpeg/VAAPI/QSV 硬件编码器检测。",
            new Dictionary<string, string>
            {
                ["preferredEncoder"] = capabilities.PreferredHardwareEncoder ?? "",
                ["preferredDecoder"] = capabilities.PreferredHardwareDecoder ?? "",
                ["accelerators"] = string.Join(",", capabilities.HardwareAccelerators),
                ["decoders"] = string.Join(",", capabilities.HardwareDecoders),
                ["encoders"] = string.Join(",", capabilities.HardwareEncoders),
                ["detectedAccelerators"] = string.Join(",", detectedAccelerators),
                ["detectedDecoders"] = string.Join(",", detectedDecoders),
                ["detectedEncoders"] = string.Join(",", detectedEncoders)
            });
    }

    private static IReadOnlyList<string> ResolveRelevantHardwareEncoders(
        FfmpegTranscodeCapabilities capabilities,
        IReadOnlyList<string> detectedEncoders)
    {
        var relevant = capabilities.HardwareEncoders
            .Concat(capabilities.HardwareEncoderProbeErrors?.Keys ?? Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return relevant.Length > 0 ? relevant : detectedEncoders;
    }

    private static string FormatHardwareKinds(IReadOnlyList<string> codecs)
    {
        var kinds = codecs
            .Select(ResolveHardwareKind)
            .Where(static kind => !string.IsNullOrWhiteSpace(kind))
            .Select(static kind => kind!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static kind => kind switch
            {
                "vaapi" => "VAAPI",
                "qsv" => "QSV",
                "cuda" => "CUDA/NVENC",
                "v4l2m2m" => "V4L2 M2M",
                "videotoolbox" => "VideoToolbox",
                _ => kind
            })
            .ToArray();

        return kinds.Length == 0 ? "无" : string.Join(", ", kinds);
    }

    private static string? FormatHardwareProbeErrors(IReadOnlyDictionary<string, string>? errors)
    {
        if (errors is null || errors.Count == 0)
        {
            return null;
        }

        return string.Join("；", errors
            .GroupBy(static pair => ResolveHardwareKind(pair.Key) ?? pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Take(4)
            .Select(static pair => SummarizeHardwareProbeError(pair.Key, pair.Value)));
    }

    private static string SummarizeHardwareProbeError(string encoder, string error)
    {
        var kind = ResolveHardwareKind(encoder);
        if (string.Equals(kind, "vaapi", StringComparison.OrdinalIgnoreCase))
        {
            return error.Contains("Input/output error", StringComparison.OrdinalIgnoreCase)
                   || error.Contains("Failed to initialise VAAPI", StringComparison.OrdinalIgnoreCase)
                   || error.Contains("No VA display found", StringComparison.OrdinalIgnoreCase)
                ? $"VAAPI：{ResolveVaapiDevicePath()} 初始化失败，通常是 NAS 显卡驱动不可用、驱动不匹配，或该设备不支持 VAAPI 编码"
                : $"VAAPI：{TrimHardwareProbeError(error)}";
        }

        if (string.Equals(kind, "qsv", StringComparison.OrdinalIgnoreCase))
        {
            return $"QSV：{TrimHardwareProbeError(error)}";
        }

        if (string.Equals(kind, "cuda", StringComparison.OrdinalIgnoreCase))
        {
            return "CUDA/NVENC：未发现可用 NVIDIA 编码设备";
        }

        if (string.Equals(kind, "v4l2m2m", StringComparison.OrdinalIgnoreCase))
        {
            return "V4L2 M2M：未发现可用 /dev/video* 编码设备";
        }

        return $"{encoder}：{TrimHardwareProbeError(error)}";
    }

    private static string TrimHardwareProbeError(string error)
    {
        var normalized = string.Join(' ', error.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 120 ? normalized : $"{normalized[..120]}...";
    }

    private static string? ResolveHardwareKind(string? codecName)
    {
        if (string.IsNullOrWhiteSpace(codecName))
        {
            return null;
        }

        var normalized = codecName.Trim().ToLowerInvariant();
        if (normalized.EndsWith("_vaapi", StringComparison.Ordinal))
        {
            return "vaapi";
        }

        if (normalized.EndsWith("_qsv", StringComparison.Ordinal))
        {
            return "qsv";
        }

        if (normalized.EndsWith("_cuvid", StringComparison.Ordinal)
            || normalized.EndsWith("_nvenc", StringComparison.Ordinal))
        {
            return "cuda";
        }

        if (normalized.EndsWith("_v4l2m2m", StringComparison.Ordinal))
        {
            return "v4l2m2m";
        }

        if (normalized.EndsWith("_videotoolbox", StringComparison.Ordinal))
        {
            return "videotoolbox";
        }

        return null;
    }

    private static IReadOnlyList<string> MissingCommonHardwareDecoders(FfmpegTranscodeCapabilities capabilities)
    {
        var codecs = new[] { "h264", "hevc", "vp9", "av1" };
        return codecs
            .Where(codec => !HasHardwareDecodePath(capabilities, codec))
            .ToArray();
    }

    private static bool HasHardwareDecodePath(FfmpegTranscodeCapabilities capabilities, string codec)
    {
        if (capabilities.HardwareDecoders.Any(decoder => decoder.StartsWith($"{codec}_", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return capabilities.HardwareAccelerators.Any(static accelerator =>
            accelerator is "vaapi" or "qsv" or "cuda" or "videotoolbox");
    }

    private static string DescribeEncoderCompatibility(string? encoder)
    {
        if (string.IsNullOrWhiteSpace(encoder))
        {
            return string.Empty;
        }

        if (encoder.StartsWith("h264_", StringComparison.OrdinalIgnoreCase))
        {
            return "（H.264，浏览器兼容性最好）";
        }

        if (encoder.StartsWith("hevc_", StringComparison.OrdinalIgnoreCase))
        {
            return "（HEVC/H.265，适合 Android TV 或支持 HEVC 的客户端，浏览器兼容性取决于客户端）";
        }

        if (encoder.StartsWith("av1_", StringComparison.OrdinalIgnoreCase))
        {
            return "（AV1，适合支持 AV1 的客户端）";
        }

        return string.Empty;
    }

    private static RuntimeSelfCheckItem? CheckHardwareDeviceAccess(FfmpegTranscodeCapabilities capabilities)
    {
        var usesDriDevice = capabilities.HardwareAccelerators.Any(static accelerator =>
                                accelerator is "vaapi" or "qsv")
                            || (capabilities.DetectedHardwareAccelerators?.Any(static accelerator =>
                                accelerator is "vaapi" or "qsv") ?? false)
                            || capabilities.HardwareDecoders.Any(static decoder =>
                                decoder.EndsWith("_vaapi", StringComparison.OrdinalIgnoreCase)
                                || decoder.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase))
                            || (capabilities.DetectedHardwareDecoders?.Any(static decoder =>
                                decoder.EndsWith("_vaapi", StringComparison.OrdinalIgnoreCase)
                                || decoder.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase)) ?? false)
                            || capabilities.HardwareEncoders.Any(static encoder =>
                                encoder.EndsWith("_vaapi", StringComparison.OrdinalIgnoreCase)
                                || encoder.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase))
                            || (capabilities.DetectedHardwareEncoders?.Any(static encoder =>
                                encoder.EndsWith("_vaapi", StringComparison.OrdinalIgnoreCase)
                                || encoder.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase)) ?? false);
        var usesV4l2Device = capabilities.HardwareAccelerators.Any(static accelerator => accelerator == "v4l2m2m")
                             || capabilities.HardwareDecoders.Any(static decoder =>
                                 decoder.EndsWith("_v4l2m2m", StringComparison.OrdinalIgnoreCase))
                             || capabilities.HardwareEncoders.Any(static encoder =>
                                 encoder.EndsWith("_v4l2m2m", StringComparison.OrdinalIgnoreCase));
        if (!usesDriDevice && !usesV4l2Device)
        {
            return null;
        }

        List<string> details = [];
        var processUser = GetProcessUserName();
        Dictionary<string, string> data = new(StringComparer.Ordinal)
        {
            ["user"] = processUser
        };
        var status = "ok";
        HardwareDeviceAccessCheck? driCheck = null;
        if (usesDriDevice)
        {
            driCheck = CheckDeviceFile(ResolveVaapiDevicePath());
            details.Add(driCheck.Detail);
            data["dri"] = driCheck.Path;
            if (!driCheck.IsAccessible)
            {
                status = "warn";
            }
        }

        if (usesV4l2Device)
        {
            var v4l2Check = CheckFirstDeviceFile("/dev", "video*");
            data["v4l2"] = v4l2Check.Path;

            if (usesDriDevice && driCheck?.IsAccessible == true)
            {
                details.Add(v4l2Check.IsAccessible
                    ? $"{v4l2Check.Detail}；当前硬件主路径已使用 {driCheck.Path}（VAAPI/QSV），/dev/video* 只是 V4L2 M2M 备用路径。"
                    : $"当前硬件主路径已使用 {driCheck.Path}（VAAPI/QSV），不需要 /dev/video*。/dev/video* 是 V4L2 M2M 设备路径，通常用于 ARM 或部分专用硬件平台。");
            }
            else
            {
                details.Add(v4l2Check.Detail);
                if (!v4l2Check.IsAccessible)
                {
                    status = "warn";
                }
            }
        }

        return new RuntimeSelfCheckItem(
            "hardware-device",
            "硬件设备权限",
            status,
            string.Join("；", details),
            data);
    }

    private static HardwareDeviceAccessCheck CheckFirstDeviceFile(string directory, string pattern)
    {
        IReadOnlyList<string> paths;
        try
        {
            paths = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, pattern).Order(StringComparer.Ordinal).ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HardwareDeviceAccessCheck(
                string.Empty,
                false,
                $"无法枚举 {directory}/{pattern}：{ex.Message}");
        }

        if (paths.Count == 0)
        {
            return new HardwareDeviceAccessCheck(
                string.Empty,
                false,
                $"FFmpeg 暴露了 v4l2m2m 能力，但未发现 {directory}/{pattern}。套件可能无法使用 V4L2 硬解/硬编。");
        }

        foreach (var path in paths)
        {
            var check = CheckDeviceFile(path);
            if (check.IsAccessible)
            {
                return check;
            }
        }

        return new HardwareDeviceAccessCheck(
            string.Join(",", paths),
            false,
            $"发现 {string.Join(", ", paths)}，但当前套件账号 {GetProcessUserName()} 无法访问。请用 DSM/SSH 管理员权限把该账号加入 video/render 设备权限，或给设备节点添加 ACL。套件自身不会以 root 提权。");
    }

    private static HardwareDeviceAccessCheck CheckDeviceFile(string devicePath)
    {
        if (!File.Exists(devicePath))
        {
            return new HardwareDeviceAccessCheck(
                devicePath,
                false,
                $"未发现 {devicePath}。套件可能无法使用对应硬解/硬编设备。");
        }

        try
        {
            using var stream = File.Open(devicePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return new HardwareDeviceAccessCheck(
                devicePath,
                true,
                $"可访问：{devicePath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HardwareDeviceAccessCheck(
                devicePath,
                false,
                $"当前套件账号 {GetProcessUserName()} 无法访问 {devicePath}：{ex.Message}。请用 DSM/SSH 管理员权限把该账号加入 video/render 设备权限，或给设备节点添加 ACL。套件自身不会以 root 提权。");
        }
    }

    private static string GetProcessUserName()
    {
        return Environment.UserName;
    }

    private static string ResolveVaapiDevicePath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("OMNIPLAY_VAAPI_DEVICE");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        try
        {
            var renderDevice = Directory.Exists("/dev/dri")
                ? Directory.EnumerateFiles("/dev/dri", "renderD*").Order(StringComparer.Ordinal).FirstOrDefault()
                : null;
            if (!string.IsNullOrWhiteSpace(renderDevice))
            {
                return renderDevice;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall back to the common Linux render node below.
        }

        return "/dev/dri/renderD128";
    }

    private sealed record HardwareDeviceAccessCheck(
        string Path,
        bool IsAccessible,
        string Detail);

    private async Task<RuntimeSelfCheckItem> CheckWebDavRangeAsync(CancellationToken cancellationToken)
    {
        var row = await ReadFirstWebDavVideoAsync(cancellationToken);
        if (row is null)
        {
            return new RuntimeSelfCheckItem(
                "webdav-range",
                "WebDAV Range",
                "ok",
                "未发现 WebDAV 视频文件，已跳过 Range 检测。只有使用 WebDAV 媒体源时才需要该检测。");
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
