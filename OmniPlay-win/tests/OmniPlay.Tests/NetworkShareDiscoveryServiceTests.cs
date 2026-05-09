using System.Net;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Infrastructure.Library;

namespace OmniPlay.Tests;

public sealed class NetworkShareDiscoveryServiceTests
{
    [Fact]
    public async Task DiscoverAsync_ReturnsManualNetworkEntries()
    {
        var service = new NetworkShareDiscoveryService(
            new HttpClient(new EmptyHandler()),
            _ => Task.FromResult<IReadOnlyList<Uri>>([]));

        var sources = await service.DiscoverAsync();

        Assert.Contains(sources, source =>
            string.Equals(source.ProtocolType, "webdav", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(source.BaseUrl, "https://", StringComparison.Ordinal));
        Assert.Contains(sources, source =>
            string.Equals(source.ProtocolType, "smb", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(source.BaseUrl, @"\\server\share", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverAsync_DetectsMediaServersAndIgnoresGenericHttpPrinter()
    {
        var endpoints = new[]
        {
            new Uri("http://plex.local:32400"),
            new Uri("http://emby.local:8096"),
            new Uri("http://jellyfin.local:8096"),
            new Uri("http://printer.local:80")
        };
        var service = new NetworkShareDiscoveryService(
            new HttpClient(new DelegateHandler(request =>
            {
                if (request.RequestUri?.Host == "plex.local" &&
                    request.RequestUri.AbsolutePath == "/identity")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""<MediaContainer machineIdentifier="abc" />""")
                    };
                }

                if (request.RequestUri?.Host == "emby.local" &&
                    request.RequestUri.AbsolutePath == "/System/Info/Public")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"ProductName":"Emby Server","ServerName":"Emby"}""")
                    };
                }

                if (request.RequestUri?.Host == "jellyfin.local" &&
                    request.RequestUri.AbsolutePath == "/System/Info/Public")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"ProductName":"Jellyfin Server","ServerName":"Jellyfin"}""")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html>printer status</html>")
                };
            })),
            _ => Task.FromResult<IReadOnlyList<Uri>>(endpoints));

        var sources = await service.DiscoverAsync();

        Assert.Contains(sources, source =>
            source.ProtocolKind == MediaSourceProtocol.Plex &&
            string.Equals(source.BaseUrl, "http://plex.local:32400", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(sources, source =>
            source.ProtocolKind == MediaSourceProtocol.Emby &&
            string.Equals(source.BaseUrl, "http://emby.local:8096", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(sources, source =>
            source.ProtocolKind == MediaSourceProtocol.Jellyfin &&
            string.Equals(source.BaseUrl, "http://jellyfin.local:8096", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sources, source =>
            source.BaseUrl.Contains("printer.local", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MediaServerDiscoveryClient_SendsPlexTokenAsHeader()
    {
        var sawTokenHeader = false;
        var client = new MediaServerDiscoveryClient(new HttpClient(new DelegateHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/library/sections")
            {
                sawTokenHeader = request.Headers.TryGetValues("X-Plex-Token", out var values) &&
                                 Assert.Single(values) == "token";
                Assert.DoesNotContain("X-Plex-Token", request.RequestUri.Query, StringComparison.OrdinalIgnoreCase);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """<MediaContainer><Directory key="1" type="movie" title="Movies" /></MediaContainer>""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    <MediaContainer>
                      <Video title="Movie">
                        <Media><Part key="/library/parts/1/file.mkv" file="/movies/Movie.2020.mkv" size="123" /></Media>
                      </Video>
                    </MediaContainer>
                    """)
            };
        })));
        var source = new MediaSource
        {
            Name = "Plex",
            ProtocolType = "plex",
            BaseUrl = "http://plex.local:32400",
            AuthConfig = MediaSourceAuthConfigSerializer.SerializeMediaServer(new MediaServerAuthConfig("token"))
        };

        var files = await client.EnumerateFilesAsync(source);

        Assert.True(sawTokenHeader);
        var file = Assert.Single(files);
        Assert.Equal("Movie.2020.mkv", file.FileName);
    }

    [Fact]
    public async Task MediaServerDiscoveryClient_UsesHlsOnlyForEmbyCompatibleDiscMedia()
    {
        var client = new MediaServerDiscoveryClient(new HttpClient(new DelegateHandler(request =>
        {
            Assert.Equal("/Items", request.RequestUri?.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "Items": [
                        {
                          "Id": "mkv123",
                          "Name": "Movie",
                          "Type": "Movie",
                          "Path": "/movies/Movie.2020.mkv",
                          "MediaSources": [
                            { "Id": "mkv123", "Path": "/movies/Movie.2020.mkv", "Container": "mkv", "Size": 123 }
                          ]
                        },
                        {
                          "Id": "iso123",
                          "Name": "Reservoir Dogs",
                          "Type": "Movie",
                          "Path": "/movies/Reservoir.Dogs.1992.iso",
                          "MediaSources": [
                            { "Id": "isoSource", "Path": "/movies/Reservoir.Dogs.1992.iso", "Container": "iso", "Size": 456 }
                          ]
                        },
                        {
                          "Id": "bdmv123",
                          "Name": "我们最后一次做孩子",
                          "Type": "Movie",
                          "Path": "/movies/我们最后一次做孩子.L'ultima.volta.che.siamo.stati.bambini.2023.1080p.Blu-ray.AVC.DTS-HD MA 5.1-nan@LuckDIY",
                          "MediaSources": [
                            { "Id": "bdmvSource", "Path": "/movies/我们最后一次做孩子.L'ultima.volta.che.siamo.stati.bambini.2023.1080p.Blu-ray.AVC.DTS-HD MA 5.1-nan@LuckDIY", "Container": "bluray", "Size": 789 }
                          ]
                        }
                      ]
                    }
                    """)
            };
        })));
        var source = new MediaSource
        {
            Name = "Emby",
            ProtocolType = "emby",
            BaseUrl = "http://emby.local:8096",
            AuthConfig = MediaSourceAuthConfigSerializer.SerializeMediaServer(new MediaServerAuthConfig("token"))
        };

        var files = await client.EnumerateFilesAsync(source);

        var mkv = Assert.Single(files, file => file.FileName == "Movie.2020.mkv");
        Assert.Equal("Items/mkv123/Download", mkv.RelativePath);

        var iso = Assert.Single(files, file => file.FileName == "Reservoir.Dogs.1992.iso");
        Assert.StartsWith("Videos/iso123/master.m3u8?", iso.RelativePath);
        Assert.Contains("MediaSourceId=isoSource", iso.RelativePath);
        Assert.Contains("EnableAutoStreamCopy=true", iso.RelativePath);
        Assert.Contains("AllowVideoStreamCopy=true", iso.RelativePath);
        Assert.Contains("AllowAudioStreamCopy=true", iso.RelativePath);
        Assert.Contains("EnableAdaptiveBitrateStreaming=false", iso.RelativePath);
        Assert.Contains("VideoCodec=h264%2Chevc", iso.RelativePath);
        Assert.Contains("AudioCodec=aac%2Cac3%2Ceac3%2Cdts%2Cflac%2Ctruehd%2Cmp3%2Copus%2Cvorbis", iso.RelativePath);
        Assert.Contains("VideoBitRate=35000000", iso.RelativePath);
        Assert.Contains("MaxStreamingBitrate=45000000", iso.RelativePath);
        Assert.Contains("AudioBitRate=640000", iso.RelativePath);
        Assert.Contains("MaxWidth=1920", iso.RelativePath);
        Assert.Contains("MaxHeight=1080", iso.RelativePath);
        Assert.DoesNotContain("/Download", iso.RelativePath, StringComparison.OrdinalIgnoreCase);

        var bdmv = Assert.Single(files, file => file.FileName == "我们最后一次做孩子.L'ultima.volta.che.siamo.stati.bambini.2023.1080p.Blu-ray.AVC.DTS-HD MA 5.1-nan@LuckDIY");
        Assert.StartsWith("Videos/bdmv123/master.m3u8?", bdmv.RelativePath);
        Assert.Contains("MediaSourceId=bdmvSource", bdmv.RelativePath);
        Assert.Contains("EnableAutoStreamCopy=true", bdmv.RelativePath);
        Assert.Contains("AllowVideoStreamCopy=true", bdmv.RelativePath);
        Assert.Contains("AllowAudioStreamCopy=true", bdmv.RelativePath);
        Assert.Contains("VideoBitRate=35000000", bdmv.RelativePath);
        Assert.Contains("MaxStreamingBitrate=45000000", bdmv.RelativePath);
        Assert.DoesNotContain("/Download", bdmv.RelativePath, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class EmptyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
