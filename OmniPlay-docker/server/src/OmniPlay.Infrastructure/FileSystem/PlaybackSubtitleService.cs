using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.Library;

namespace OmniPlay.Infrastructure.FileSystem;

public sealed class PlaybackSubtitleService : IPlaybackSubtitleService
{
    private static readonly HttpMethod PropFind = new("PROPFIND");
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".vtt", ".ass", ".ssa", ".sup"
    };
    private static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3gp", ".avi", ".bdmv", ".divx", ".flv", ".iso", ".m2ts", ".m4v", ".mkv", ".mov",
        ".mp4", ".mpeg", ".mpg", ".mts", ".rmvb", ".ts", ".vob", ".webm", ".wmv"
    };
    private static readonly Regex SeasonEpisodeRegex = new(
        @"(?<![a-z0-9])s(?<season>\d{1,2})e(?<episode>\d{1,3})(?![a-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly SqliteDatabase database;
    private readonly IStoragePaths storagePaths;
    private readonly HttpClient httpClient;

    public PlaybackSubtitleService(
        SqliteDatabase database,
        IStoragePaths storagePaths,
        HttpClient httpClient)
    {
        this.database = database;
        this.storagePaths = storagePaths;
        this.httpClient = httpClient;
    }

    public async Task<IReadOnlyList<PlaybackSubtitleTrack>?> DiscoverAsync(
        string videoFileId,
        CancellationToken cancellationToken = default)
    {
        var row = await ReadRowAsync(videoFileId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var subtitles = IsWebDav(row)
            ? await DiscoverWebDavSubtitlesAsync(row, cancellationToken)
            : DiscoverLocalSubtitles(row);

        return subtitles is null
            ? null
            : subtitles.Select(subtitle => new PlaybackSubtitleTrack(
                subtitle.Id,
                subtitle.FileName,
                Path.GetExtension(subtitle.FileName).TrimStart('.').ToLowerInvariant(),
                subtitle.Language,
                subtitle.CanServeWebVtt
                    ? $"/api/playback/files/{Uri.EscapeDataString(row.Id)}/subtitles/{Uri.EscapeDataString(subtitle.Id)}.vtt"
                    : null,
                true))
            .ToArray();
    }

    public async Task<string?> ResolveSubtitlePathAsync(
        string videoFileId,
        string? subtitleId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subtitleId))
        {
            return null;
        }

        var row = await ReadRowAsync(videoFileId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        if (!IsWebDav(row))
        {
            var localSubtitle = DiscoverLocalSubtitles(row)?
                .FirstOrDefault(subtitle => string.Equals(subtitle.Id, subtitleId, StringComparison.Ordinal));
            return localSubtitle?.FullPath;
        }

        var remoteSubtitle = (await DiscoverWebDavSubtitlesAsync(row, cancellationToken))
            .FirstOrDefault(subtitle => string.Equals(subtitle.Id, subtitleId, StringComparison.Ordinal));
        if (remoteSubtitle is null || string.IsNullOrWhiteSpace(remoteSubtitle.RemoteUrl))
        {
            return null;
        }

        var cachePath = ResolveWebDavSubtitleCachePath(row, remoteSubtitle);
        if (WebDavPlaybackCacheService.IsCacheUsable(cachePath, remoteSubtitle.ContentLength))
        {
            WebDavPlaybackCacheService.TouchLastAccessTime(cachePath);
            return cachePath;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var tempPath = $"{cachePath}.{Guid.NewGuid():N}.part";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, remoteSubtitle.RemoteUrl);
            WebDavPlaybackCacheService.ApplyBasicAuth(request, row.Username, ReadPassword(row.SecretJson));
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
            WebDavPlaybackCacheService.TouchLastAccessTime(cachePath);
            return cachePath;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private async Task<SubtitleRow?> ReadRowAsync(
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
                   vf.relative_path,
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

        return new SubtitleRow(
            reader.GetString(0),
            reader.GetString(1),
            MediaNameParser.NormalizeRelativePath(reader.GetString(2)),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    private static IReadOnlyList<DiscoveredSubtitleFile>? DiscoverLocalSubtitles(SubtitleRow row)
    {
        var videoPath = ResolveLocalVideoPath(row);
        if (videoPath is null)
        {
            return null;
        }

        var directory = Path.GetDirectoryName(videoPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        var videoBaseName = Path.GetFileNameWithoutExtension(videoPath);
        var videoFileCount = Directory.EnumerateFiles(directory)
            .Count(path => SupportedVideoExtensions.Contains(Path.GetExtension(path)));
        return Directory.EnumerateFiles(directory)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .Where(path => IsSubtitleCandidateForVideo(videoBaseName, Path.GetFileNameWithoutExtension(path), videoFileCount))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(path => new DiscoveredSubtitleFile(
                StableSubtitleId(Path.GetFileName(path)),
                Path.GetFileName(path),
                path,
                RemoteUrl: null,
                ContentLength: new FileInfo(path).Length,
                GuessSubtitleLanguage(videoBaseName, Path.GetFileNameWithoutExtension(path)),
                CanServeWebVtt(path)))
            .ToArray();
    }

    private async Task<IReadOnlyList<DiscoveredSubtitleFile>> DiscoverWebDavSubtitlesAsync(
        SubtitleRow row,
        CancellationToken cancellationToken)
    {
        var parentUri = BuildWebDavParentUri(row.SourceBaseUrl, row.RelativePath);
        using var request = CreatePropFindRequest(parentUri, row.Username, ReadPassword(row.SecretJson));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new UnauthorizedAccessException("WebDAV 认证失败。");
        }

        if (!IsWebDavSuccess(response.StatusCode))
        {
            return [];
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var videoBaseName = Path.GetFileNameWithoutExtension(row.FileName);
        var parentUrl = NormalizeUrl(parentUri);
        var parentPrefix = parentUrl + "/";
        var siblingResources = ParseResources(content, parentUri)
            .Where(resource => !resource.IsCollection)
            .Where(resource => !string.Equals(resource.Url, parentUrl, StringComparison.OrdinalIgnoreCase))
            .Where(resource => resource.Url.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase))
            .Where(resource => !resource.Url[parentPrefix.Length..].Contains('/', StringComparison.Ordinal))
            .ToArray();
        var videoFileCount = siblingResources.Count(resource => SupportedVideoExtensions.Contains(Path.GetExtension(resource.Name)));
        return siblingResources
            .Where(resource => SupportedExtensions.Contains(Path.GetExtension(resource.Name)))
            .Where(resource => IsSubtitleCandidateForVideo(videoBaseName, Path.GetFileNameWithoutExtension(resource.Name), videoFileCount))
            .OrderBy(resource => resource.Name, StringComparer.OrdinalIgnoreCase)
            .Select(resource => new DiscoveredSubtitleFile(
                StableSubtitleId(resource.Url),
                resource.Name,
                FullPath: null,
                resource.Url,
                resource.ContentLength,
                GuessSubtitleLanguage(videoBaseName, Path.GetFileNameWithoutExtension(resource.Name)),
                CanServeWebVtt(resource.Name)))
            .ToArray();
    }

    private static string? ResolveLocalVideoPath(SubtitleRow row)
    {
        var sourceRoot = Path.GetFullPath(row.SourceBaseUrl);
        var relativePath = row.RelativePath.Replace('/', Path.DirectorySeparatorChar);
        var absolutePath = Path.GetFullPath(Path.Combine(sourceRoot, relativePath));
        return IsPathInsideRoot(sourceRoot, absolutePath) && File.Exists(absolutePath)
            ? absolutePath
            : null;
    }

    private string ResolveWebDavSubtitleCachePath(SubtitleRow row, DiscoveredSubtitleFile subtitle)
    {
        var key = string.Join('|', row.Id, subtitle.RemoteUrl, subtitle.ContentLength?.ToString() ?? "");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        var extension = Path.GetExtension(subtitle.FileName);
        if (extension.Length > 16 || extension.Contains(Path.DirectorySeparatorChar) || extension.Contains(Path.AltDirectorySeparatorChar))
        {
            extension = ".sub";
        }

        return Path.Combine(storagePaths.CacheDirectory, "webdav", "subtitles", $"{hash}{extension}");
    }

    private static HttpRequestMessage CreatePropFindRequest(Uri uri, string? username, string? password)
    {
        var request = new HttpRequestMessage(PropFind, uri);
        request.Headers.Add("Depth", "1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        WebDavPlaybackCacheService.ApplyBasicAuth(request, username, password);
        request.Content = new StringContent(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <D:propfind xmlns:D="DAV:">
              <D:prop>
                <D:displayname/>
                <D:resourcetype/>
                <D:getcontentlength/>
                <D:getlastmodified/>
              </D:prop>
            </D:propfind>
            """,
            Encoding.UTF8,
            "application/xml");
        return request;
    }

    private static IReadOnlyList<WebDavResourceEntry> ParseResources(string content, Uri requestedUri)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var document = XDocument.Parse(content);
        List<WebDavResourceEntry> entries = [];
        foreach (var response in document.Descendants().Where(static element => element.Name.LocalName == "response"))
        {
            var href = response.Elements().FirstOrDefault(static element => element.Name.LocalName == "href")?.Value;
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            var entryUri = ResolveHref(requestedUri, href);
            var prop = response.Descendants().FirstOrDefault(static element => element.Name.LocalName == "prop");
            var isCollection = prop?.Descendants().Any(static element => element.Name.LocalName == "collection") == true;
            var displayName = prop?.Elements().FirstOrDefault(static element => element.Name.LocalName == "displayname")?.Value;
            entries.Add(new WebDavResourceEntry(
                string.IsNullOrWhiteSpace(displayName) ? GuessName(entryUri) : WebUtility.HtmlDecode(displayName.Trim()),
                NormalizeUrl(entryUri),
                isCollection,
                ParseContentLength(prop)));
        }

        return entries;
    }

    private static Uri BuildWebDavParentUri(string sourceBaseUrl, string relativePath)
    {
        var normalizedRelativePath = MediaNameParser.NormalizeRelativePath(relativePath);
        var lastSlash = normalizedRelativePath.LastIndexOf('/');
        var directoryRelativePath = lastSlash >= 0 ? normalizedRelativePath[..lastSlash] : string.Empty;
        var baseUrl = sourceBaseUrl.TrimEnd('/') + "/";
        var escapedDirectory = string.Join(
            '/',
            directoryRelativePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
        var uri = string.IsNullOrWhiteSpace(escapedDirectory)
            ? new Uri(baseUrl, UriKind.Absolute)
            : new Uri(new Uri(baseUrl, UriKind.Absolute), escapedDirectory + "/");
        return uri;
    }

    private static Uri ResolveHref(Uri requestedUri, string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var absoluteUri)
            && (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps))
        {
            return absoluteUri;
        }

        return new Uri(requestedUri, href);
    }

    private static string GuessName(Uri uri)
    {
        var path = uri.AbsolutePath.TrimEnd('/');
        var lastSlash = path.LastIndexOf('/');
        var name = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
        return string.IsNullOrWhiteSpace(name) ? uri.Host : Uri.UnescapeDataString(name);
    }

    private static long? ParseContentLength(XElement? prop)
    {
        var value = prop?.Elements().FirstOrDefault(static element => element.Name.LocalName == "getcontentlength")?.Value;
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)
            ? length
            : null;
    }

    private static string NormalizeUrl(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty,
            Query = string.Empty,
            UserName = string.Empty,
            Password = string.Empty
        };

        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static bool IsWebDavSuccess(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MultiStatus or HttpStatusCode.OK;
    }

    private static bool IsWebDav(SubtitleRow row)
    {
        return string.Equals(row.SourceKind, "webdav", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanServeWebVtt(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".srt", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".vtt", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".ass", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".ssa", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".sup", StringComparison.OrdinalIgnoreCase);
    }

    private static string StableSubtitleId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return $"sub_{Convert.ToHexString(hash)[..12].ToLowerInvariant()}";
    }

    private static string? GuessSubtitleLanguage(string videoBaseName, string subtitleBaseName)
    {
        var suffix = subtitleBaseName.StartsWith(videoBaseName, StringComparison.OrdinalIgnoreCase)
            ? subtitleBaseName[videoBaseName.Length..].Trim('.', '-', '_', ' ')
            : null;
        var languageSource = string.IsNullOrWhiteSpace(suffix) ? subtitleBaseName : suffix;
        return ResolveSubtitleLanguage(languageSource)
               ?? (string.IsNullOrWhiteSpace(suffix) ? null : suffix);
    }

    private static bool IsSubtitleCandidateForVideo(string videoBaseName, string subtitleBaseName, int videoFileCount)
    {
        if (subtitleBaseName.StartsWith(videoBaseName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (videoFileCount == 1)
        {
            return true;
        }

        var videoEpisodeKey = ResolveSeasonEpisodeKey(videoBaseName);
        if (videoEpisodeKey is null)
        {
            return false;
        }

        return string.Equals(videoEpisodeKey, ResolveSeasonEpisodeKey(subtitleBaseName), StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveSeasonEpisodeKey(string value)
    {
        var match = SeasonEpisodeRegex.Match(value);
        if (!match.Success)
        {
            return null;
        }

        var season = int.Parse(match.Groups["season"].Value, CultureInfo.InvariantCulture);
        var episode = int.Parse(match.Groups["episode"].Value, CultureInfo.InvariantCulture);
        return FormattableString.Invariant($"S{season:00}E{episode:000}");
    }

    private static string? ResolveSubtitleLanguage(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var tokens = normalized
            .Split(['.', '_', '-', ' ', '[', ']', '(', ')', '+', '&', ',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Any(static token => token is "zh" or "zho" or "chi" or "chs" or "cht" or "cmn" or "yue" or "cn" or "chn"
                or "zhcn" or "zhtw" or "zhhk" or "sc" or "tc" or "gb" or "big5" or "mandarin" or "cantonese" or "chinese")
            || normalized.Contains("中文", StringComparison.Ordinal)
            || normalized.Contains("国语", StringComparison.Ordinal)
            || normalized.Contains("國語", StringComparison.Ordinal)
            || normalized.Contains("普通话", StringComparison.Ordinal)
            || normalized.Contains("普通話", StringComparison.Ordinal)
            || normalized.Contains("粤语", StringComparison.Ordinal)
            || normalized.Contains("粵語", StringComparison.Ordinal)
            || normalized.Contains("简体", StringComparison.Ordinal)
            || normalized.Contains("簡體", StringComparison.Ordinal)
            || normalized.Contains("繁体", StringComparison.Ordinal)
            || normalized.Contains("繁體", StringComparison.Ordinal)
            || normalized.Contains("中字", StringComparison.Ordinal)
            || normalized.Contains("双语", StringComparison.Ordinal)
            || normalized.Contains("雙語", StringComparison.Ordinal))
        {
            return "zh";
        }

        if (tokens.Any(static token => token is "en" or "eng" or "english" or "us" or "usa" or "uk")
            || normalized.Contains("英语", StringComparison.Ordinal)
            || normalized.Contains("英文", StringComparison.Ordinal)
            || normalized.Contains("英語", StringComparison.Ordinal))
        {
            return "en";
        }

        if (tokens.Any(static token => token is "ja" or "jp" or "jpn" or "japanese")
            || normalized.Contains("日本", StringComparison.Ordinal)
            || normalized.Contains("日语", StringComparison.Ordinal)
            || normalized.Contains("日語", StringComparison.Ordinal)
            || normalized.Contains("日文", StringComparison.Ordinal))
        {
            return "ja";
        }

        return null;
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

    private sealed record SubtitleRow(
        string Id,
        string FileName,
        string RelativePath,
        string SourceKind,
        string SourceBaseUrl,
        string? Username,
        string? SecretJson);

    private sealed record DiscoveredSubtitleFile(
        string Id,
        string FileName,
        string? FullPath,
        string? RemoteUrl,
        long? ContentLength,
        string? Language,
        bool CanServeWebVtt);

    private sealed record WebDavResourceEntry(
        string Name,
        string Url,
        bool IsCollection,
        long? ContentLength);
}
