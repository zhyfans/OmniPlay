namespace OmniPlay.Core.Models.Entities;

public static class MediaSourcePathResolver
{
    public static string ResolvePlaybackPath(string? protocolType, string? baseUrl, string? relativePath)
    {
        return ResolvePlaybackPath(ParseProtocolKind(protocolType), baseUrl, relativePath);
    }

    public static string ResolveAuthenticatedPlaybackPath(
        string? protocolType,
        string? baseUrl,
        string? relativePath,
        string? authConfig)
    {
        return ResolveAuthenticatedPlaybackPath(ParseProtocolKind(protocolType), baseUrl, relativePath, authConfig);
    }

    public static string ResolvePlaybackPath(MediaSourceProtocol? protocol, string? baseUrl, string? relativePath)
    {
        var normalizedRelativePath = NormalizeRelativePath(relativePath);

        return protocol switch
        {
            MediaSourceProtocol.Local => ResolveLocalPath(baseUrl, normalizedRelativePath),
            MediaSourceProtocol.WebDav => ResolveWebDavUrl(baseUrl, normalizedRelativePath),
            MediaSourceProtocol.Smb => ResolveLocalPath(baseUrl, normalizedRelativePath),
            MediaSourceProtocol.Direct => normalizedRelativePath,
            MediaSourceProtocol.Plex or MediaSourceProtocol.Emby or MediaSourceProtocol.Jellyfin =>
                ResolveMediaServerUrl(baseUrl, normalizedRelativePath),
            _ => string.Empty
        };
    }

    public static string ResolveAuthenticatedPlaybackPath(
        MediaSourceProtocol? protocol,
        string? baseUrl,
        string? relativePath,
        string? authConfig)
    {
        var normalizedRelativePath = NormalizeRelativePath(relativePath);

        return protocol switch
        {
            MediaSourceProtocol.WebDav => ResolveWebDavUrl(
                baseUrl,
                normalizedRelativePath,
                MediaSourceAuthConfigSerializer.DeserializeWebDav(authConfig),
                includeCredentials: true),
            MediaSourceProtocol.Plex => AppendQueryParameterIfMissing(
                AppendQueryParameterIfMissing(
                    ResolveMediaServerUrl(baseUrl, normalizedRelativePath),
                    "download",
                    "1"),
                "X-Plex-Token",
                MediaSourceAuthConfigSerializer.DeserializeMediaServer(authConfig)?.Token),
            MediaSourceProtocol.Emby or MediaSourceProtocol.Jellyfin => AppendQueryParameter(
                ResolveMediaServerUrl(baseUrl, normalizedRelativePath),
                "api_key",
                MediaSourceAuthConfigSerializer.DeserializeMediaServer(authConfig)?.Token),
            _ => ResolvePlaybackPath(protocol, baseUrl, relativePath)
        };
    }

    public static string ResolveMetadataPath(string? protocolType, string? baseUrl, string? relativePath)
    {
        return ResolveMetadataPath(ParseProtocolKind(protocolType), baseUrl, relativePath);
    }

    public static string ResolveMetadataPath(MediaSourceProtocol? protocol, string? baseUrl, string? relativePath)
    {
        var playbackPath = ResolvePlaybackPath(protocol, baseUrl, relativePath);
        if (protocol == MediaSourceProtocol.WebDav &&
            Uri.TryCreate(playbackPath, UriKind.Absolute, out var playbackUri))
        {
            return Uri.UnescapeDataString(playbackUri.AbsolutePath);
        }

        return playbackPath;
    }

    public static string NormalizeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        var normalized = relativePath
            .Trim()
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized.TrimStart('/');
    }

    public static bool IsRemoteHttpUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    public static bool IsMediaServerPlaybackEndpointPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            candidate = uri.AbsolutePath;
        }

        var pathOnly = candidate.Split('?', 2)[0]
            .Replace('\\', '/')
            .Trim('/');
        if (string.IsNullOrWhiteSpace(pathOnly))
        {
            return false;
        }

        var parts = pathOnly
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static part => part.ToLowerInvariant())
            .ToArray();

        return parts is ["items", _, "download", ..] ||
               parts is ["videos", _, "master.m3u8", ..] ||
               (parts.Length >= 3 &&
                parts[0] == "videos" &&
                parts[2].StartsWith("stream.", StringComparison.Ordinal)) ||
               parts is ["library", "parts", ..] ||
               parts is ["library", "metadata", ..];
    }

    public static bool IsPlayableLocation(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               (IsRemoteHttpUrl(value) || File.Exists(value));
    }

    public static string GetDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (IsRemoteHttpUrl(value) && Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            var lastSegment = uri.Segments.LastOrDefault()?.Trim('/');
            if (!string.IsNullOrWhiteSpace(lastSegment))
            {
                return Uri.UnescapeDataString(lastSegment);
            }

            return uri.Host;
        }

        return Path.GetFileName(value);
    }

    private static string ResolveLocalPath(string? baseUrl, string normalizedRelativePath)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return normalizedRelativePath;
        }

        if (string.IsNullOrWhiteSpace(normalizedRelativePath))
        {
            return baseUrl;
        }

        return Path.Combine(baseUrl, normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string ResolveWebDavUrl(
        string? baseUrl,
        string normalizedRelativePath,
        WebDavAuthConfig? authConfig = null,
        bool includeCredentials = false)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return string.Empty;
        }

        var normalizedBaseUrl = MediaSourceNormalizer.NormalizeBaseUrl(MediaSourceProtocol.WebDav, baseUrl);
        if (string.IsNullOrWhiteSpace(normalizedRelativePath))
        {
            return normalizedBaseUrl;
        }

        if (!Uri.TryCreate(AppendTrailingSlash(normalizedBaseUrl), UriKind.Absolute, out var baseUri))
        {
            return normalizedBaseUrl;
        }

        var encodedRelativePath = string.Join(
            '/',
            normalizedRelativePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));

        var resourceUri = new Uri(baseUri, encodedRelativePath);
        if (!includeCredentials ||
            authConfig is null ||
            (string.IsNullOrWhiteSpace(authConfig.Username) && string.IsNullOrEmpty(authConfig.Password)))
        {
            return resourceUri.AbsoluteUri;
        }

        var builder = new UriBuilder(resourceUri)
        {
            UserName = authConfig.Username ?? string.Empty,
            Password = authConfig.Password ?? string.Empty
        };
        return builder.Uri.AbsoluteUri;
    }

    private static string ResolveMediaServerUrl(string? baseUrl, string normalizedRelativePath)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return string.Empty;
        }

        var normalizedBaseUrl = baseUrl.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(normalizedRelativePath))
        {
            return normalizedBaseUrl;
        }

        if (!Uri.TryCreate(AppendTrailingSlash(normalizedBaseUrl), UriKind.Absolute, out var baseUri))
        {
            return normalizedBaseUrl;
        }

        return new Uri(baseUri, normalizedRelativePath.TrimStart('/')).AbsoluteUri;
    }

    private static string AppendQueryParameter(string url, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(value))
        {
            return url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var separator = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
        return $"{url}{separator}{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value.Trim())}";
    }

    private static string AppendQueryParameterIfMissing(string url, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(value))
        {
            return url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var needle = $"{Uri.EscapeDataString(name)}=";
        var hasParameter = uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.StartsWith(needle, StringComparison.OrdinalIgnoreCase));
        return hasParameter ? url : AppendQueryParameter(url, name, value);
    }

    private static string AppendTrailingSlash(string value)
    {
        return value.EndsWith("/", StringComparison.Ordinal) ? value : $"{value}/";
    }

    private static MediaSourceProtocol? ParseProtocolKind(string? protocolType)
    {
        return protocolType?.Trim().ToLowerInvariant() switch
        {
            "local" => MediaSourceProtocol.Local,
            "webdav" => MediaSourceProtocol.WebDav,
            "smb" => MediaSourceProtocol.Smb,
            "direct" => MediaSourceProtocol.Direct,
            "plex" => MediaSourceProtocol.Plex,
            "emby" => MediaSourceProtocol.Emby,
            "jellyfin" => MediaSourceProtocol.Jellyfin,
            _ => null
        };
    }
}
