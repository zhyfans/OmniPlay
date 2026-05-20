using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Core.Models.Network;

namespace OmniPlay.Infrastructure.Library;

public interface IMediaServerDiscoveryClient
{
    Task<IReadOnlyList<MediaServerFileEntry>> EnumerateFilesAsync(
        MediaSource source,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NetworkShareFolderItem>> ListLibrariesAsync(
        MediaSource source,
        CancellationToken cancellationToken = default);
}

public sealed record MediaServerFileEntry(
    string MetadataPath,
    string RelativePath,
    string FileName,
    long ContentLength,
    string? MediaType = null);

    public sealed class MediaServerDiscoveryClient : IMediaServerDiscoveryClient
    {
        private const string PlexClientIdentifier = "omniplay-windows";

        private static readonly HashSet<string> MediaFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".m2ts", ".m2t", ".ts", ".m4v", ".flv", ".webm", ".rmvb"
        };

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient;

    public MediaServerDiscoveryClient(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public Task<IReadOnlyList<MediaServerFileEntry>> EnumerateFilesAsync(
        MediaSource source,
        CancellationToken cancellationToken = default)
    {
        return source.ProtocolKind switch
        {
            MediaSourceProtocol.Plex => EnumeratePlexFilesAsync(source, cancellationToken),
            MediaSourceProtocol.Emby or MediaSourceProtocol.Jellyfin => EnumerateEmbyCompatibleFilesAsync(source, cancellationToken),
            _ => Task.FromResult<IReadOnlyList<MediaServerFileEntry>>([])
        };
    }

    public Task<IReadOnlyList<NetworkShareFolderItem>> ListLibrariesAsync(
        MediaSource source,
        CancellationToken cancellationToken = default)
    {
        return source.ProtocolKind switch
        {
            MediaSourceProtocol.Plex => ListPlexLibrariesAsync(source, cancellationToken),
            MediaSourceProtocol.Emby or MediaSourceProtocol.Jellyfin => ListEmbyCompatibleLibrariesAsync(source, cancellationToken),
            _ => Task.FromResult<IReadOnlyList<NetworkShareFolderItem>>([])
        };
    }

    private async Task<IReadOnlyList<MediaServerFileEntry>> EnumeratePlexFilesAsync(
        MediaSource source,
        CancellationToken cancellationToken)
    {
        var auth = MediaSourceAuthConfigSerializer.DeserializeMediaServer(source.AuthConfig);
        using var sectionsRequest = BuildRequest(source.BaseUrl, "library/sections", auth, source.ProtocolKind);
        using var sectionsResponse = await httpClient.SendAsync(sectionsRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        sectionsResponse.EnsureSuccessStatusCode();

        await using var sectionsStream = await sectionsResponse.Content.ReadAsStreamAsync(cancellationToken);
        var sectionsDocument = await XDocument.LoadAsync(sectionsStream, LoadOptions.None, cancellationToken);
        var sections = sectionsDocument
            .Descendants("Directory")
            .Select(element => new
            {
                Key = ((string?)element.Attribute("key"))?.Trim(),
                Type = ((string?)element.Attribute("type"))?.Trim().ToLowerInvariant()
            })
            .Where(section => !string.IsNullOrWhiteSpace(section.Key))
            .ToList();

        List<MediaServerFileEntry> results = [];
        var selectedLibraryId = auth?.LibraryId?.Trim();
        foreach (var section in sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(selectedLibraryId) &&
                !string.Equals(section.Key, selectedLibraryId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var itemType = section.Type == "show" ? "4" : "1";
            using var sectionRequest = BuildRequest(
                source.BaseUrl,
                $"library/sections/{Uri.EscapeDataString(section.Key!)}/all?type={itemType}",
                auth,
                source.ProtocolKind);
            using var response = await httpClient.SendAsync(sectionRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
            foreach (var video in document.Descendants("Video"))
            {
                var title = ((string?)video.Attribute("title"))?.Trim()
                            ?? ((string?)video.Attribute("grandparentTitle"))?.Trim()
                            ?? "Plex";
                var seriesTitle = ((string?)video.Attribute("grandparentTitle"))?.Trim();
                var seasonNumber = ParseNullableInt((string?)video.Attribute("parentIndex"));
                var episodeNumber = ParseNullableInt((string?)video.Attribute("index"));
                foreach (var part in video.Descendants("Part"))
                {
                    var key = ((string?)part.Attribute("key"))?.Trim();
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    var filePath = ((string?)part.Attribute("file"))?.Trim();
                    var fileName = ResolveFileName(filePath, title);
                    var metadataPath = section.Type == "show"
                        ? BuildTvEpisodeMetadataPath(seriesTitle, seasonNumber, episodeNumber, fileName)
                          ?? (string.IsNullOrWhiteSpace(filePath) ? fileName : filePath)
                        : string.IsNullOrWhiteSpace(filePath) ? fileName : filePath;
                    results.Add(new MediaServerFileEntry(
                        metadataPath,
                        key.TrimStart('/'),
                        fileName,
                        ParseLong((string?)part.Attribute("size")),
                        section.Type == "show" ? "tv" : "movie"));
                }
            }
        }

        return results;
    }

    private async Task<IReadOnlyList<MediaServerFileEntry>> EnumerateEmbyCompatibleFilesAsync(
        MediaSource source,
        CancellationToken cancellationToken)
    {
        var auth = MediaSourceAuthConfigSerializer.DeserializeMediaServer(source.AuthConfig);
        if (!string.IsNullOrWhiteSpace(auth?.UserId))
        {
            auth = await ResolveEmbyCompatibleAuthAsync(source, auth, auth.UserId, cancellationToken);
        }

        var queryFields = "Path,MediaSources,SeriesName,ParentIndexNumber,IndexNumber";
        var queryPrefix = string.IsNullOrWhiteSpace(auth?.LibraryId)
            ? $"Recursive=true&IncludeItemTypes=Movie,Episode&Fields={queryFields}"
            : $"ParentId={Uri.EscapeDataString(auth.LibraryId!.Trim())}&Recursive=true&IncludeItemTypes=Movie,Episode&Fields={queryFields}";
        var relativePath = string.IsNullOrWhiteSpace(auth?.UserId)
            ? $"Items?{queryPrefix}"
            : $"Users/{Uri.EscapeDataString(auth.UserId!)}/Items?{queryPrefix}";
        using var request = BuildEmbyCompatibleRequest(source.BaseUrl, relativePath, auth, source.ProtocolKind);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<EmbyItemsResponse>(stream, JsonOptions, cancellationToken);
        if (payload?.Items is null || payload.Items.Count == 0)
        {
            return [];
        }

        List<MediaServerFileEntry> results = [];
        foreach (var item in payload.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Id))
            {
                continue;
            }

            var mediaSource = item.MediaSources?.FirstOrDefault();
            var sourcePath = mediaSource?.Path ?? item.Path;
            var fileName = ResolveFileName(sourcePath, item.Name ?? item.Id);
            var metadataPath = string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase)
                ? BuildTvEpisodeMetadataPath(item.SeriesName, item.ParentIndexNumber, item.IndexNumber, fileName)
                  ?? sourcePath
                  ?? item.Name
                  ?? item.Id
                : sourcePath ?? item.Name ?? item.Id;
            results.Add(new MediaServerFileEntry(
                metadataPath,
                ResolveEmbyCompatiblePlaybackPath(item.Id, mediaSource?.Id, fileName, metadataPath, mediaSource?.Container),
                fileName,
                mediaSource?.Size ?? 0,
                string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie"));
        }

        return results;
    }

    private static string ResolveEmbyCompatiblePlaybackPath(string itemId, string? mediaSourceId, string fileName, string? mediaPath, string? container)
    {
        if (!IsEmbyCompatibleDiscImageOrFolder(fileName, mediaPath, container))
        {
            return $"Items/{Uri.EscapeDataString(itemId)}/Download";
        }

        var escapedItemId = Uri.EscapeDataString(itemId);
        var resolvedMediaSourceId = string.IsNullOrWhiteSpace(mediaSourceId) ? itemId : mediaSourceId.Trim();
        var playSessionId = $"omniplay{new string(itemId.Where(char.IsLetterOrDigit).ToArray())}";
        return $"Videos/{escapedItemId}/master.m3u8?{BuildEmbyCompatibleHlsQuery(resolvedMediaSourceId, playSessionId, "omniplay-windows", fileName)}";
    }

    private static string BuildEmbyCompatibleHlsQuery(string mediaSourceId, string playSessionId, string deviceId, string fileName)
    {
        var quality = ResolveEmbyCompatibleHlsQuality(fileName);
        return string.Join(
            '&',
            $"MediaSourceId={Uri.EscapeDataString(mediaSourceId)}",
            $"PlaySessionId={Uri.EscapeDataString(playSessionId)}",
            $"DeviceId={Uri.EscapeDataString(deviceId)}",
            "EnableAutoStreamCopy=true",
            "AllowVideoStreamCopy=true",
            "AllowAudioStreamCopy=true",
            "EnableAdaptiveBitrateStreaming=false",
            $"VideoCodec={Uri.EscapeDataString("h264,hevc")}",
            $"AudioCodec={Uri.EscapeDataString("aac,ac3,eac3,dts,flac,truehd,mp3,opus,vorbis")}",
            "SegmentContainer=ts",
            "SegmentLength=6",
            "MinSegments=1",
            $"VideoBitRate={quality.VideoBitRate}",
            $"MaxStreamingBitrate={quality.MaxStreamingBitrate}",
            "AudioBitRate=640000",
            $"MaxWidth={quality.MaxWidth}",
            $"MaxHeight={quality.MaxHeight}",
            "Profile=high",
            "Level=51",
            "RequireAvc=false",
            "TranscodingMaxAudioChannels=6",
            "BreakOnNonKeyFrames=false",
            "CopyTimestamps=true",
            "Context=Streaming");
    }

    private static (int VideoBitRate, int MaxStreamingBitrate, int MaxWidth, int MaxHeight) ResolveEmbyCompatibleHlsQuality(string fileName)
    {
        return fileName.Contains("2160p", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("uhd", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("4k", StringComparison.OrdinalIgnoreCase)
            ? (60_000_000, 70_000_000, 3840, 2160)
            : (35_000_000, 45_000_000, 1920, 1080);
    }

    private static bool IsEmbyCompatibleDiscImageOrFolder(string fileName, string? mediaPath, string? container)
    {
        var extension = Path.GetExtension(fileName);
        if (string.Equals(extension, ".iso", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var lowerPath = mediaPath?.ToLowerInvariant() ?? string.Empty;
        if (lowerPath.Contains("/bdmv", StringComparison.Ordinal) ||
            lowerPath.Contains("\\bdmv", StringComparison.Ordinal))
        {
            return true;
        }

        var lowerContainer = container?.ToLowerInvariant() ?? string.Empty;
        if (lowerContainer.Contains("iso", StringComparison.Ordinal) ||
            lowerContainer.Contains("bluray", StringComparison.Ordinal) ||
            lowerContainer.Contains("bdmv", StringComparison.Ordinal))
        {
            return true;
        }

        if (MediaFileExtensions.Contains(extension))
        {
            return false;
        }

        var lowerName = fileName.ToLowerInvariant();
        return lowerName.Contains("bdmv", StringComparison.Ordinal) ||
               lowerName.Contains("blu-ray", StringComparison.Ordinal) ||
               lowerName.Contains("bluray", StringComparison.Ordinal) ||
               lowerName.Contains("uhd", StringComparison.Ordinal);
    }

    private async Task<IReadOnlyList<NetworkShareFolderItem>> ListPlexLibrariesAsync(
        MediaSource source,
        CancellationToken cancellationToken)
    {
        var auth = MediaSourceAuthConfigSerializer.DeserializeMediaServer(source.AuthConfig);
        using var request = BuildRequest(source.BaseUrl, "library/sections", auth, source.ProtocolKind);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        var normalizedBaseUrl = MediaSourceNormalizer.NormalizeBaseUrl(source.ProtocolKind, source.BaseUrl);
        return document
            .Descendants("Directory")
            .Select(element => new
            {
                Key = ((string?)element.Attribute("key"))?.Trim(),
                Name = ((string?)element.Attribute("title"))?.Trim()
                       ?? ((string?)element.Attribute("name"))?.Trim()
                       ?? "Plex 媒体库",
                Type = ((string?)element.Attribute("type"))?.Trim()
            })
            .Where(library => !string.IsNullOrWhiteSpace(library.Key))
            .Select(library => CreateMediaServerFolder(
                source,
                normalizedBaseUrl,
                library.Name,
                library.Key,
                library.Type,
                auth))
            .OrderBy(static folder => folder.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<NetworkShareFolderItem>> ListEmbyCompatibleLibrariesAsync(
        MediaSource source,
        CancellationToken cancellationToken)
    {
        var auth = MediaSourceAuthConfigSerializer.DeserializeMediaServer(source.AuthConfig);
        var normalizedBaseUrl = MediaSourceNormalizer.NormalizeBaseUrl(source.ProtocolKind, source.BaseUrl);
        var userInput = auth?.UserId?.Trim();
        var resolvedAuth = await ResolveEmbyCompatibleAuthAsync(source, auth, userInput, cancellationToken);
        var resolvedUserId = resolvedAuth?.UserId?.Trim() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(resolvedUserId))
        {
            try
            {
                using var request = BuildEmbyCompatibleRequest(source.BaseUrl, $"Users/{Uri.EscapeDataString(resolvedUserId)}/Views", resolvedAuth, source.ProtocolKind);
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var payload = await JsonSerializer.DeserializeAsync<EmbyViewsResponse>(stream, JsonOptions, cancellationToken);
                var folders = payload?.Items?
                    .Where(static item => !string.IsNullOrWhiteSpace(item.Id))
                    .Select(item => CreateMediaServerFolder(source, normalizedBaseUrl, item.Name ?? $"{source.ProtocolLabel} 媒体库", item.Id, item.CollectionType, resolvedAuth))
                    .OrderBy(static folder => folder.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (folders is { Count: > 0 })
                {
                    return folders;
                }
            }
            catch (HttpRequestException)
                when (!string.IsNullOrWhiteSpace(userInput) &&
                      string.Equals(resolvedUserId, userInput, StringComparison.OrdinalIgnoreCase) &&
                      !LooksLikeEmbyCompatibleUserId(userInput))
            {
                if (await TryCanUseEmbyCompatibleItemsAsync(source, resolvedAuth, cancellationToken))
                {
                    return
                    [
                        CreateMediaServerFolder(source, normalizedBaseUrl, "全部媒体库", null, null, resolvedAuth)
                    ];
                }

                throw new InvalidOperationException($"无法解析 {source.ProtocolLabel} 用户“{userInput}”。请确认用户名，以及下方填写的是密码、访问令牌或 API Key。");
            }
            catch (HttpRequestException ex) when (IsAuthenticationFailureStatus(ex.StatusCode))
            {
                if (await TryCanUseEmbyCompatibleItemsAsync(source, resolvedAuth, cancellationToken))
                {
                    return
                    [
                        CreateMediaServerFolder(source, normalizedBaseUrl, "全部媒体库", null, null, resolvedAuth)
                    ];
                }

                throw new InvalidOperationException($"{source.ProtocolLabel} 认证失败：请检查用户名，以及下方填写的是密码、访问令牌或 API Key。");
            }
        }

        try
        {
            using var request = BuildEmbyCompatibleRequest(source.BaseUrl, "Library/VirtualFolders", resolvedAuth, source.ProtocolKind);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var virtualFolders = await JsonSerializer.DeserializeAsync<List<EmbyVirtualFolder>>(stream, JsonOptions, cancellationToken);
            var folders = virtualFolders?
                .Select(folder => new
                {
                    Id = FirstNonEmpty(folder.Id, folder.ItemId),
                    Name = folder.Name,
                    folder.CollectionType
                })
                .Where(static folder => !string.IsNullOrWhiteSpace(folder.Id))
                .Select(folder => CreateMediaServerFolder(source, normalizedBaseUrl, folder.Name ?? $"{source.ProtocolLabel} 媒体库", folder.Id, folder.CollectionType, resolvedAuth))
                .OrderBy(static folder => folder.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (folders is { Count: > 0 })
            {
                return folders;
            }
        }
        catch (HttpRequestException ex) when (string.IsNullOrWhiteSpace(resolvedUserId) && IsWholeLibraryFallbackStatus(ex.StatusCode))
        {
        }
        catch (HttpRequestException ex) when (IsAuthenticationFailureStatus(ex.StatusCode))
        {
            if (await TryCanUseEmbyCompatibleItemsAsync(source, resolvedAuth, cancellationToken))
            {
                return
                [
                    CreateMediaServerFolder(source, normalizedBaseUrl, "全部媒体库", null, null, resolvedAuth)
                ];
            }

            throw new InvalidOperationException($"{source.ProtocolLabel} 认证失败：请检查用户名，以及下方填写的是密码、访问令牌或 API Key。");
        }
        catch (JsonException) when (string.IsNullOrWhiteSpace(resolvedUserId))
        {
        }

        return
        [
            CreateMediaServerFolder(source, normalizedBaseUrl, "全部媒体库", null, null, resolvedAuth)
        ];
    }

    private async Task<MediaServerAuthConfig?> ResolveEmbyCompatibleAuthAsync(
        MediaSource source,
        MediaServerAuthConfig? auth,
        string? userInput,
        CancellationToken cancellationToken)
    {
        var credential = auth?.Token?.Trim() ?? string.Empty;
        var trimmed = userInput?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(credential))
        {
            return auth;
        }

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            var currentUser = await TryFetchCurrentEmbyCompatibleUserAsync(source, auth, cancellationToken);
            return auth is null ? null : auth with { UserId = currentUser?.Id?.Trim() ?? string.Empty };
        }

        var current = await TryFetchCurrentEmbyCompatibleUserAsync(source, auth, cancellationToken);
        if (current is not null &&
            (string.Equals(current.Id, trimmed, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(current.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return auth is null ? null : auth with { UserId = current.Id?.Trim() ?? string.Empty };
        }

        if (LooksLikeEmbyCompatibleUserId(trimmed))
        {
            return auth is null ? null : auth with { UserId = trimmed };
        }

        var passwordAuth = await TryAuthenticateEmbyCompatibleUserAsync(source, trimmed, credential, cancellationToken);
        if (passwordAuth is not null)
        {
            return passwordAuth;
        }

        var users = await TryFetchEmbyCompatibleUsersAsync(source, auth, cancellationToken);
        var matched = users.FirstOrDefault(user =>
            string.Equals(user.Id, trimmed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(user.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(matched?.Id))
        {
            return auth is null ? null : auth with { UserId = matched.Id.Trim() };
        }

        if (await TryCanUseWholeEmbyCompatibleLibraryAsync(source, auth, cancellationToken) ||
            await TryCanUseEmbyCompatibleItemsAsync(source, auth, cancellationToken))
        {
            return auth is null ? null : auth with { UserId = string.Empty };
        }

        throw new InvalidOperationException($"{source.ProtocolLabel} 认证失败：请检查用户名，以及下方填写的是密码、访问令牌或 API Key。");
    }

    private async Task<EmbyUser?> TryFetchCurrentEmbyCompatibleUserAsync(
        MediaSource source,
        MediaServerAuthConfig? auth,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = BuildEmbyCompatibleRequest(source.BaseUrl, "Users/Me", auth, source.ProtocolKind);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var user = await JsonSerializer.DeserializeAsync<EmbyUser>(stream, JsonOptions, cancellationToken);
            return string.IsNullOrWhiteSpace(user?.Id) ? null : user;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<EmbyUser>> TryFetchEmbyCompatibleUsersAsync(
        MediaSource source,
        MediaServerAuthConfig? auth,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = BuildEmbyCompatibleRequest(source.BaseUrl, "Users", auth, source.ProtocolKind);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<List<EmbyUser>>(stream, JsonOptions, cancellationToken) ?? [];
        }
        catch (HttpRequestException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<bool> TryCanUseWholeEmbyCompatibleLibraryAsync(
        MediaSource source,
        MediaServerAuthConfig? auth,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = BuildEmbyCompatibleRequest(source.BaseUrl, "Library/VirtualFolders", auth, source.ProtocolKind);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private async Task<bool> TryCanUseEmbyCompatibleItemsAsync(
        MediaSource source,
        MediaServerAuthConfig? auth,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = BuildEmbyCompatibleRequest(
                source.BaseUrl,
                "Items?Recursive=true&IncludeItemTypes=Movie,Episode&Limit=1",
                auth,
                source.ProtocolKind);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private async Task<MediaServerAuthConfig?> TryAuthenticateEmbyCompatibleUserAsync(
        MediaSource source,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildUri(source.BaseUrl, "Users/AuthenticateByName", null, source.ProtocolKind));
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("X-Emby-Authorization", CreateEmbyAuthorizationHeader(source.ProtocolLabel));
        request.Content = new StringContent(
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["Username"] = username,
                ["Pw"] = password,
                ["Password"] = password
            }),
            Encoding.UTF8,
            "application/json");

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<EmbyAuthenticationResponse>(stream, JsonOptions, cancellationToken);
            if (string.IsNullOrWhiteSpace(payload?.AccessToken))
            {
                return null;
            }

            return new MediaServerAuthConfig(
                payload.AccessToken.Trim(),
                payload.User?.Id?.Trim() ?? string.Empty);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool LooksLikeEmbyCompatibleUserId(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 16 && trimmed.All(static character =>
            char.IsAsciiHexDigit(character) || character == '-');
    }

    private static bool IsWholeLibraryFallbackStatus(System.Net.HttpStatusCode? statusCode)
    {
        return statusCode is System.Net.HttpStatusCode.NotFound;
    }

    private static bool IsAuthenticationFailureStatus(System.Net.HttpStatusCode? statusCode)
    {
        return statusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;
    }

    private static string CreateEmbyAuthorizationHeader(string protocolLabel)
    {
        return $"MediaBrowser Client=\"{protocolLabel}\", Device=\"{protocolLabel}\", DeviceId=\"omniplay-windows\", Version=\"1.0\"";
    }

    private static NetworkShareFolderItem CreateMediaServerFolder(
        MediaSource source,
        string normalizedBaseUrl,
        string name,
        string? libraryId,
        string? libraryType,
        MediaServerAuthConfig? auth)
    {
        var displayName = string.IsNullOrWhiteSpace(name) ? $"{source.ProtocolLabel} 媒体库" : name.Trim();
        return new NetworkShareFolderItem
        {
            Name = $"{source.ProtocolLabel} · {displayName}",
            ProtocolType = source.ProtocolType,
            BaseUrl = normalizedBaseUrl,
            Description = normalizedBaseUrl,
            AuthConfig = MediaSourceAuthConfigSerializer.SerializeMediaServer(new MediaServerAuthConfig(
                auth?.Token ?? string.Empty,
                auth?.UserId,
                libraryId,
                displayName,
                libraryType))
        };
    }

    private static Uri BuildUri(
        string baseUrl,
        string relativePath,
        MediaServerAuthConfig? auth,
        MediaSourceProtocol? protocol,
        bool includePlexTokenQuery = true)
    {
        var normalizedBaseUrl = MediaSourceNormalizer.NormalizeBaseUrl(protocol, baseUrl);
        if (!Uri.TryCreate($"{normalizedBaseUrl.TrimEnd('/')}/", UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("媒体服务器地址无效。");
        }

        var separator = relativePath.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var authenticatedPath = protocol == MediaSourceProtocol.Plex
            ? includePlexTokenQuery
                ? AppendQuery(relativePath, separator, "X-Plex-Token", auth?.Token)
                : relativePath
            : AppendQuery(relativePath, separator, "api_key", auth?.Token);
        return new Uri(baseUri, authenticatedPath.TrimStart('/'));
    }

    private static HttpRequestMessage BuildRequest(
        string baseUrl,
        string relativePath,
        MediaServerAuthConfig? auth,
        MediaSourceProtocol? protocol)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            BuildUri(
                baseUrl,
                relativePath,
                auth,
                protocol,
                includePlexTokenQuery: protocol != MediaSourceProtocol.Plex));

        if (protocol == MediaSourceProtocol.Plex)
        {
            ApplyPlexHeaders(request, auth?.Token);
        }

        return request;
    }

    private static HttpRequestMessage BuildEmbyCompatibleRequest(
        string baseUrl,
        string relativePath,
        MediaServerAuthConfig? auth,
        MediaSourceProtocol? protocol)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            BuildUri(baseUrl, relativePath, auth, protocol));
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation(
            "X-Emby-Authorization",
            CreateEmbyAuthorizationHeader(protocol == MediaSourceProtocol.Jellyfin ? "Jellyfin" : "Emby"));
        if (!string.IsNullOrWhiteSpace(auth?.Token))
        {
            request.Headers.TryAddWithoutValidation("X-Emby-Token", auth.Token.Trim());
            request.Headers.TryAddWithoutValidation("X-MediaBrowser-Token", auth.Token.Trim());
        }

        return request;
    }

    private static void ApplyPlexHeaders(HttpRequestMessage request, string? token)
    {
        request.Headers.Accept.ParseAdd("application/xml");
        request.Headers.TryAddWithoutValidation("X-Plex-Product", "OmniPlay");
        request.Headers.TryAddWithoutValidation("X-Plex-Version", "1.0");
        request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", PlexClientIdentifier);
        request.Headers.TryAddWithoutValidation("X-Plex-Device-Name", "OmniPlay");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation("X-Plex-Token", token.Trim());
        }
    }

    private static string AppendQuery(string relativePath, string separator, string name, string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? relativePath
            : $"{relativePath}{separator}{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value.Trim())}";
    }

    private static string ResolveFileName(string? path, string fallback)
    {
        var fileName = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            return fileName;
        }

        return string.IsNullOrWhiteSpace(fallback)
            ? "media"
            : fallback.Trim();
    }

    private static long ParseLong(string? value)
    {
        return long.TryParse(value, out var parsed) ? parsed : 0;
    }

    private static int? ParseNullableInt(string? value)
    {
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;
    }

    private static string? BuildTvEpisodeMetadataPath(
        string? seriesTitle,
        int? seasonNumber,
        int? episodeNumber,
        string fallbackName)
    {
        var title = SanitizeVirtualPathSegment(seriesTitle);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var extension = Path.GetExtension(fallbackName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".mkv";
        }

        var episodeToken = seasonNumber.HasValue && episodeNumber.HasValue
            ? $"S{seasonNumber.Value:00}E{episodeNumber.Value:00}"
            : episodeNumber.HasValue
                ? $"E{episodeNumber.Value:00}"
                : null;
        var fileStem = episodeToken is null
            ? SanitizeVirtualPathSegment(Path.GetFileNameWithoutExtension(fallbackName))
            : $"{title}.{episodeToken}";
        if (string.IsNullOrWhiteSpace(fileStem))
        {
            fileStem = title;
        }

        var seasonFolder = seasonNumber.HasValue
            ? $"Season {seasonNumber.Value}"
            : "Season 1";
        return $"{title}/{seasonFolder}/{fileStem}{extension}";
    }

    private static string? SanitizeVirtualPathSegment(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            trimmed = trimmed.Replace(invalid, ' ');
        }

        trimmed = trimmed.Replace('/', ' ').Replace('\\', ' ');
        return string.Join(' ', trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }

    private sealed class EmbyItemsResponse
    {
        public List<EmbyItem> Items { get; init; } = [];
    }

    private sealed class EmbyViewsResponse
    {
        public List<EmbyLibraryItem> Items { get; init; } = [];
    }

    private sealed class EmbyLibraryItem
    {
        public string? Id { get; init; }

        public string? Name { get; init; }

        public string? CollectionType { get; init; }
    }

    private sealed class EmbyUser
    {
        public string? Id { get; init; }

        public string? Name { get; init; }
    }

    private sealed record EmbyAuthenticateByNameRequest(string Username, string Pw);

    private sealed class EmbyAuthenticationResponse
    {
        public string? AccessToken { get; init; }

        public EmbyUser? User { get; init; }
    }

    private sealed class EmbyVirtualFolder
    {
        public string? Id { get; init; }

        public string? ItemId { get; init; }

        public string? Name { get; init; }

        public string? CollectionType { get; init; }
    }

    private sealed class EmbyItem
    {
        public string? Id { get; init; }

        public string? Name { get; init; }

        public string? Path { get; init; }

        public string? Type { get; init; }

        public string? SeriesName { get; init; }

        public int? ParentIndexNumber { get; init; }

        public int? IndexNumber { get; init; }

        public List<EmbyMediaSource>? MediaSources { get; init; }
    }

    private sealed class EmbyMediaSource
    {
        public string? Id { get; init; }

        public string? Container { get; init; }

        public string? Path { get; init; }

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long Size { get; init; }
    }
}
