using OmniPlay.Core.Models.Entities;

namespace OmniPlay.Tests;

public sealed class MediaSourcePathResolverTests
{
    [Fact]
    public void ResolveAuthenticatedPlaybackPath_AddsPlexDownloadAndTokenParameters()
    {
        var authConfig = MediaSourceAuthConfigSerializer.SerializeMediaServer(new MediaServerAuthConfig("token value"));

        var path = MediaSourcePathResolver.ResolveAuthenticatedPlaybackPath(
            MediaSourceProtocol.Plex,
            "http://127.0.0.1:32400",
            "/library/parts/15/1761294834/file.mkv",
            authConfig);

        Assert.Equal(
            "http://127.0.0.1:32400/library/parts/15/1761294834/file.mkv?download=1&X-Plex-Token=token%20value",
            path);
    }

    [Fact]
    public void ResolveAuthenticatedPlaybackPath_PreservesJellyfinHlsQueryAndAddsApiKey()
    {
        var authConfig = MediaSourceAuthConfigSerializer.SerializeMediaServer(new MediaServerAuthConfig("api value"));

        var path = MediaSourcePathResolver.ResolveAuthenticatedPlaybackPath(
            MediaSourceProtocol.Jellyfin,
            "http://192.168.0.150:8096",
            "Videos/218fe7342f645e3cf86f563115495a67/master.m3u8?mediaSourceId=218fe7342f645e3cf86f563115495a67&playSessionId=omniplay218fe7342f645e3cf86f563115495a67",
            authConfig);

        Assert.Equal(
            "http://192.168.0.150:8096/Videos/218fe7342f645e3cf86f563115495a67/master.m3u8?mediaSourceId=218fe7342f645e3cf86f563115495a67&playSessionId=omniplay218fe7342f645e3cf86f563115495a67&api_key=api%20value",
            path);
    }
}
