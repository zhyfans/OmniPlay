using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.Library;

namespace OmniPlay.Infrastructure.FileSystem;

public sealed class WebDavRangeStreamService : IWebDavRangeStreamService
{
    private const long SegmentSizeBytes = 8L * 1024 * 1024;

    private readonly SqliteDatabase database;
    private readonly IStoragePaths storagePaths;
    private readonly HttpClient httpClient;

    public WebDavRangeStreamService(
        SqliteDatabase database,
        IStoragePaths storagePaths,
        HttpClient httpClient)
    {
        this.database = database;
        this.storagePaths = storagePaths;
        this.httpClient = httpClient;
    }

    public async Task<PlayableVideoFile?> GetFileInfoAsync(
        string videoFileId,
        CancellationToken cancellationToken = default)
    {
        var row = await ReadRowAsync(videoFileId, cancellationToken);
        if (row is null || !IsWebDav(row))
        {
            return null;
        }

        return CreatePlayableInfo(row);
    }

    public async Task<WebDavRangeStreamResult?> OpenReadAsync(
        string videoFileId,
        string? rangeHeader,
        CancellationToken cancellationToken = default)
    {
        var row = await ReadRowAsync(videoFileId, cancellationToken);
        if (row is null || !IsWebDav(row))
        {
            return null;
        }

        var contentType = ResolveContentType(row.FileName);
        var range = ResolveRange(rangeHeader, row.FileSizeBytes);
        if (range.IsInvalid)
        {
            return new WebDavRangeStreamResult(
                416,
                null,
                contentType,
                null,
                row.FileSizeBytes.HasValue ? $"bytes */{row.FileSizeBytes.Value}" : "bytes */*",
                "Range 请求超出文件大小。");
        }

        if (!range.HasRange)
        {
            return await OpenRemotePassThroughAsync(row, contentType, cancellationToken);
        }

        if (range.IsOpenEnded)
        {
            return await OpenRemoteRangePassThroughAsync(row, range.Start, contentType, cancellationToken);
        }

        return await OpenRangeSegmentAsync(row, range, contentType, cancellationToken);
    }

    private async Task<WebDavRangeStreamResult> OpenRemotePassThroughAsync(
        WebDavRangeRow row,
        string contentType,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, WebDavPlaybackCacheService.BuildWebDavFileUri(row.SourceBaseUrl, row.RelativePath));
        WebDavPlaybackCacheService.ApplyBasicAuth(request, row.Username, WebDavPlaybackCacheService.ReadPassword(row.SecretJson));
        var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            return new WebDavRangeStreamResult(
                (int)response.StatusCode,
                null,
                contentType,
                null,
                null,
                $"WebDAV 读取失败：HTTP {(int)response.StatusCode}。");
        }

        var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        return new WebDavRangeStreamResult(
            (int)response.StatusCode,
            content,
            response.Content.Headers.ContentType?.ToString() ?? contentType,
            response.Content.Headers.ContentLength ?? row.FileSizeBytes,
            response.Content.Headers.ContentRange?.ToString(),
            null,
            response);
    }

    private async Task<WebDavRangeStreamResult> OpenRemoteRangePassThroughAsync(
        WebDavRangeRow row,
        long start,
        string contentType,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, WebDavPlaybackCacheService.BuildWebDavFileUri(row.SourceBaseUrl, row.RelativePath));
        request.Headers.Range = new RangeHeaderValue(start, null);
        WebDavPlaybackCacheService.ApplyBasicAuth(request, row.Username, WebDavPlaybackCacheService.ReadPassword(row.SecretJson));
        var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            var statusCode = response.StatusCode == HttpStatusCode.OK ? 502 : (int)response.StatusCode;
            response.Dispose();
            return new WebDavRangeStreamResult(
                statusCode,
                null,
                contentType,
                null,
                null,
                $"WebDAV Range 读取失败：HTTP {(int)response.StatusCode}。");
        }

        var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        return new WebDavRangeStreamResult(
            206,
            content,
            response.Content.Headers.ContentType?.ToString() ?? contentType,
            response.Content.Headers.ContentLength,
            response.Content.Headers.ContentRange?.ToString(),
            null,
            response);
    }

    private async Task<WebDavRangeStreamResult> OpenRangeSegmentAsync(
        WebDavRangeRow row,
        ResolvedRange range,
        string contentType,
        CancellationToken cancellationToken)
    {
        var cacheSegment = ResolveCacheSegment(row, range);
        var cachePath = ResolveSegmentCachePath(row, cacheSegment.CacheStart, cacheSegment.CacheEnd);
        var expectedLength = cacheSegment.CacheEnd - cacheSegment.CacheStart + 1;
        if (WebDavPlaybackCacheService.IsCacheUsable(cachePath, expectedLength))
        {
            WebDavPlaybackCacheService.TouchLastAccessTime(cachePath);
            return OpenCachedSegment(row, cachePath, cacheSegment, contentType);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var tempPath = $"{cachePath}.{Guid.NewGuid():N}.part";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, WebDavPlaybackCacheService.BuildWebDavFileUri(row.SourceBaseUrl, row.RelativePath));
            request.Headers.Range = new RangeHeaderValue(cacheSegment.CacheStart, cacheSegment.CacheEnd);
            WebDavPlaybackCacheService.ApplyBasicAuth(request, row.Username, WebDavPlaybackCacheService.ReadPassword(row.SecretJson));
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                return new WebDavRangeStreamResult(
                    response.StatusCode == HttpStatusCode.OK
                        ? 502
                        : (int)response.StatusCode,
                    null,
                    contentType,
                    null,
                    null,
                    $"WebDAV Range 读取失败：HTTP {(int)response.StatusCode}。");
            }

            await using (var remote = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var local = File.Create(tempPath))
            {
                await remote.CopyToAsync(local, cancellationToken);
            }

            File.Move(tempPath, cachePath, overwrite: true);
            WebDavPlaybackCacheService.TouchLastAccessTime(cachePath);
            return OpenCachedSegment(row, cachePath, cacheSegment, contentType);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static WebDavRangeStreamResult OpenCachedSegment(
        WebDavRangeRow row,
        string cachePath,
        CacheSegment cacheSegment,
        string contentType)
    {
        var stream = File.OpenRead(cachePath);
        var offset = cacheSegment.ResponseStart - cacheSegment.CacheStart;
        var length = cacheSegment.ResponseEnd - cacheSegment.ResponseStart + 1;
        Stream content = offset == 0 && length == stream.Length
            ? stream
            : new BoundedRangeStream(stream, offset, length);
        var total = row.FileSizeBytes.HasValue ? row.FileSizeBytes.Value.ToString() : "*";
        return new WebDavRangeStreamResult(
            206,
            content,
            contentType,
            length,
            $"bytes {cacheSegment.ResponseStart}-{cacheSegment.ResponseEnd}/{total}",
            null);
    }

    private async Task<WebDavRangeRow?> ReadRowAsync(
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

        return new WebDavRangeRow(
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

    private string ResolveSegmentCachePath(WebDavRangeRow row, long start, long end)
    {
        var key = string.Join('|', row.Id, row.RelativePath, row.FileSizeBytes?.ToString() ?? "", row.SourceBaseUrl);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(storagePaths.CacheDirectory, "webdav", "ranges", hash, $"{start}-{end}.seg");
    }

    private static CacheSegment ResolveCacheSegment(WebDavRangeRow row, ResolvedRange range)
    {
        if (range.IsOpenEnded || range.Length <= SegmentSizeBytes)
        {
            var cacheStart = range.Start / SegmentSizeBytes * SegmentSizeBytes;
            var cacheEnd = cacheStart + SegmentSizeBytes - 1;
            if (row.FileSizeBytes.HasValue)
            {
                cacheEnd = Math.Min(cacheEnd, row.FileSizeBytes.Value - 1);
            }

            if (range.HasExplicitEnd)
            {
                cacheEnd = Math.Max(cacheEnd, range.End);
                if (row.FileSizeBytes.HasValue)
                {
                    cacheEnd = Math.Min(cacheEnd, row.FileSizeBytes.Value - 1);
                }
            }

            return new CacheSegment(
                cacheStart,
                cacheEnd,
                range.Start,
                Math.Min(range.End, cacheEnd));
        }

        return new CacheSegment(range.Start, range.End, range.Start, range.End);
    }

    private static PlayableVideoFile CreatePlayableInfo(WebDavRangeRow row)
    {
        return new PlayableVideoFile(
            row.Id,
            WebDavPlaybackCacheService.BuildWebDavFileUri(row.SourceBaseUrl, row.RelativePath).AbsoluteUri,
            row.FileName,
            row.MediaKind,
            row.FileSizeBytes,
            row.DurationSeconds,
            row.Container,
            row.VideoCodec,
            row.AudioCodec,
            row.SubtitleSummary);
    }

    private static ResolvedRange ResolveRange(string? rangeHeader, long? totalLength)
    {
        if (string.IsNullOrWhiteSpace(rangeHeader))
        {
            return ResolvedRange.NoRange;
        }

        var normalized = rangeHeader.Trim();
        if (!normalized.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(',', StringComparison.Ordinal))
        {
            return ResolvedRange.Invalid;
        }

        var rangeValue = normalized["bytes=".Length..];
        var dashIndex = rangeValue.IndexOf('-');
        if (dashIndex < 0)
        {
            return ResolvedRange.Invalid;
        }

        var startText = rangeValue[..dashIndex].Trim();
        var endText = rangeValue[(dashIndex + 1)..].Trim();
        long start;
        long end;

        if (string.IsNullOrWhiteSpace(startText))
        {
            if (!totalLength.HasValue
                || !long.TryParse(endText, out var suffixLength)
                || suffixLength <= 0)
            {
                return ResolvedRange.Invalid;
            }

            start = Math.Max(totalLength.Value - suffixLength, 0);
            end = totalLength.Value - 1;
        }
        else
        {
            if (!long.TryParse(startText, out start) || start < 0)
            {
                return ResolvedRange.Invalid;
            }

            if (string.IsNullOrWhiteSpace(endText))
            {
                end = totalLength.HasValue
                    ? Math.Min(start + SegmentSizeBytes - 1, totalLength.Value - 1)
                    : start + SegmentSizeBytes - 1;
            }
            else if (!long.TryParse(endText, out end) || end < start)
            {
                return ResolvedRange.Invalid;
            }
            else if (totalLength.HasValue)
            {
                end = Math.Min(end, totalLength.Value - 1);
            }
        }

        if (totalLength.HasValue && start >= totalLength.Value)
        {
            return ResolvedRange.Invalid;
        }

        return new ResolvedRange(true, false, start, end, string.IsNullOrWhiteSpace(endText), !string.IsNullOrWhiteSpace(endText));
    }

    private static bool IsWebDav(WebDavRangeRow row)
    {
        return string.Equals(row.SourceKind, "webdav", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveContentType(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".avi" => "video/x-msvideo",
            _ => "application/octet-stream"
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

    private readonly record struct ResolvedRange(
        bool HasRange,
        bool IsInvalid,
        long Start,
        long End,
        bool IsOpenEnded,
        bool HasExplicitEnd)
    {
        public long Length => End - Start + 1;

        public static readonly ResolvedRange NoRange = new(false, false, 0, 0, false, false);

        public static readonly ResolvedRange Invalid = new(false, true, 0, 0, false, false);
    }

    private readonly record struct CacheSegment(
        long CacheStart,
        long CacheEnd,
        long ResponseStart,
        long ResponseEnd);

    private sealed class BoundedRangeStream : Stream
    {
        private readonly Stream inner;
        private long remaining;

        public BoundedRangeStream(Stream inner, long offset, long length)
        {
            this.inner = inner;
            remaining = length;
            inner.Position = offset;
        }

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (remaining <= 0)
            {
                return 0;
            }

            var read = inner.Read(buffer, offset, (int)Math.Min(count, remaining));
            remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (remaining <= 0)
            {
                return 0;
            }

            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], cancellationToken);
            remaining -= read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed record WebDavRangeRow(
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
