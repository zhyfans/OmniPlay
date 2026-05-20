using System.Net;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Infrastructure.Library;

namespace OmniPlay.Tests;

public sealed class NetworkShareDiscoveryServiceTests
{
    [Fact]
    public async Task DiscoverAsync_ReturnsOnlyDetectedNetworkEntries()
    {
        var service = new NetworkShareDiscoveryService(
            new HttpClient(new EmptyHandler()),
            _ => Task.FromResult<IReadOnlyList<Uri>>([]));

        var sources = await service.DiscoverAsync();

        Assert.DoesNotContain(sources, source =>
            string.Equals(source.BaseUrl, "http://server:5005", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sources, source =>
            string.Equals(source.BaseUrl, @"\\server\share", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DiscoverAsync_DetectsWebDavEndpointWithPort()
    {
        var endpoints = new[] { new Uri("http://webdav.local:5005") };
        var service = new NetworkShareDiscoveryService(
            new HttpClient(new DelegateHandler(request =>
            {
                if (request.Method.Method == "PROPFIND" &&
                    request.RequestUri?.Host == "webdav.local" &&
                    request.RequestUri.Port == 5005)
                {
                    var response = new HttpResponseMessage((HttpStatusCode)207)
                    {
                        Content = new StringContent("""
                        <d:multistatus xmlns:d="DAV:" />
                        """)
                    };
                    response.Headers.TryAddWithoutValidation("DAV", "1,2");
                    return response;
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            })),
            _ => Task.FromResult<IReadOnlyList<Uri>>(endpoints));

        var sources = await service.DiscoverAsync();

        Assert.Contains(sources, source =>
            source.ProtocolKind == MediaSourceProtocol.WebDav &&
            string.Equals(source.Name, "WebDAV", StringComparison.Ordinal) &&
            string.Equals(source.BaseUrl, "http://webdav.local:5005", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DiscoverAsync_DetectsProtectedWebDavOnKnownPort()
    {
        var endpoints = new[] { new Uri("http://nas.local:5005") };
        var service = new NetworkShareDiscoveryService(
            new HttpClient(new DelegateHandler(request =>
            {
                Assert.Equal("PROPFIND", request.Method.Method);
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            })),
            _ => Task.FromResult<IReadOnlyList<Uri>>(endpoints));

        var sources = await service.DiscoverAsync();

        Assert.Contains(sources, source =>
            source.ProtocolKind == MediaSourceProtocol.WebDav &&
            string.Equals(source.BaseUrl, "http://nas.local:5005", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolveSmbDisplayName_ReturnsShareNameForUncShareRoot()
    {
        var method = typeof(NetworkShareDiscoveryService).GetMethod(
            "ResolveSmbDisplayName",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var name = Assert.IsType<string>(method?.Invoke(null, [@"\\192.168.0.102\Movies"]));

        Assert.Equal("Movies", name);
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
            string.Equals(source.Name, "Plex", StringComparison.Ordinal) &&
            string.Equals(source.BaseUrl, "http://plex.local:32400", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(sources, source =>
            source.ProtocolKind == MediaSourceProtocol.Emby &&
            string.Equals(source.Name, "Emby", StringComparison.Ordinal) &&
            string.Equals(source.BaseUrl, "http://emby.local:8096", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(sources, source =>
            source.ProtocolKind == MediaSourceProtocol.Jellyfin &&
            string.Equals(source.Name, "Jellyfin", StringComparison.Ordinal) &&
            string.Equals(source.BaseUrl, "http://jellyfin.local:8096", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sources, source =>
            source.BaseUrl.Contains("printer.local", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DiscoverAsync_DetectsEmbyCompatibleServerFromPublicInfoWithoutProductName()
    {
        var endpoints = new[] { new Uri("http://emby.local:8096") };
        var service = new NetworkShareDiscoveryService(
            new HttpClient(new DelegateHandler(request =>
            {
                if (request.RequestUri?.Host == "emby.local" &&
                    request.RequestUri.AbsolutePath == "/System/Info/Public")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"ServerName":"Living Room","Id":"server-id","Version":"4.8.0.0"}""")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            })),
            _ => Task.FromResult<IReadOnlyList<Uri>>(endpoints));

        var sources = await service.DiscoverAsync();

        Assert.Contains(sources, source =>
            source.ProtocolKind == MediaSourceProtocol.Emby &&
            string.Equals(source.Name, "Emby", StringComparison.Ordinal) &&
            string.Equals(source.BaseUrl, "http://emby.local:8096", StringComparison.OrdinalIgnoreCase));
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
    public async Task MediaServerDiscoveryClient_MapsPlexEpisodesToSoftwareParseableMetadataPaths()
    {
        var client = new MediaServerDiscoveryClient(new HttpClient(new DelegateHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/library/sections")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """<MediaContainer><Directory key="2" type="show" title="Shows" /></MediaContainer>""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    <MediaContainer>
                      <Video title="Episode One" grandparentTitle="Dark" parentIndex="1" index="1">
                        <Media><Part key="/library/parts/ep1/file.mkv" file="/plex/metadata/opaque-ep1.mkv" size="123" /></Media>
                      </Video>
                      <Video title="Episode Two" grandparentTitle="Dark" parentIndex="1" index="2">
                        <Media><Part key="/library/parts/ep2/file.mkv" file="/plex/metadata/opaque-ep2.mkv" size="124" /></Media>
                      </Video>
                    </MediaContainer>
                    """)
            };
        })));
        var source = new MediaSource
        {
            Name = "Plex",
            ProtocolType = "plex",
            BaseUrl = "http://plex.local:32400"
        };

        var files = await client.EnumerateFilesAsync(source);

        Assert.Equal(
            ["Dark/Season 1/Dark.S01E01.mkv", "Dark/Season 1/Dark.S01E02.mkv"],
            files.Select(static file => file.MetadataPath).ToList());
        Assert.Equal(
            ["library/parts/ep1/file.mkv", "library/parts/ep2/file.mkv"],
            files.Select(static file => file.RelativePath).ToList());
    }

    [Fact]
    public async Task MediaServerDiscoveryClient_MapsEmbyCompatibleEpisodesToSoftwareParseableMetadataPaths()
    {
        var client = new MediaServerDiscoveryClient(new HttpClient(new DelegateHandler(request =>
        {
            Assert.Equal("/Items", request.RequestUri?.AbsolutePath);
            Assert.Contains("SeriesName", request.RequestUri?.Query, StringComparison.OrdinalIgnoreCase);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "Items": [
                        {
                          "Id": "ep1",
                          "Name": "Chapter One",
                          "Type": "Episode",
                          "SeriesName": "Dark",
                          "ParentIndexNumber": 1,
                          "IndexNumber": 1,
                          "MediaSources": [
                            { "Id": "ep1-src", "Path": "/emby/opaque-ep1.mkv", "Container": "mkv", "Size": 123 }
                          ]
                        },
                        {
                          "Id": "ep2",
                          "Name": "Chapter Two",
                          "Type": "Episode",
                          "SeriesName": "Dark",
                          "ParentIndexNumber": 1,
                          "IndexNumber": 2,
                          "MediaSources": [
                            { "Id": "ep2-src", "Path": "/emby/opaque-ep2.mkv", "Container": "mkv", "Size": 124 }
                          ]
                        }
                      ]
                    }
                    """)
            };
        })));
        var source = new MediaSource
        {
            Name = "Jellyfin",
            ProtocolType = "jellyfin",
            BaseUrl = "http://jellyfin.local:8096"
        };

        var files = await client.EnumerateFilesAsync(source);

        Assert.Equal(
            ["Dark/Season 1/Dark.S01E01.mkv", "Dark/Season 1/Dark.S01E02.mkv"],
            files.Select(static file => file.MetadataPath).ToList());
        Assert.Equal(
            ["Items/ep1/Download", "Items/ep2/Download"],
            files.Select(static file => file.RelativePath).ToList());
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

    [Fact]
    public async Task MediaServerDiscoveryClient_AuthenticatesEmbyByUsernameAndPasswordWhenListingLibraries()
    {
        var client = new MediaServerDiscoveryClient(new HttpClient(new DelegateHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath is "/Users/Me" or "/Users")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath == "/Users/AuthenticateByName")
            {
                var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
                Assert.Contains("\"Username\"", body, StringComparison.Ordinal);
                Assert.Contains("\"Pw\"", body, StringComparison.Ordinal);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "AccessToken": "session-token",
                          "User": { "Id": "user-id", "Name": "alice" }
                        }
                        """)
                };
            }

            if (request.RequestUri?.AbsolutePath == "/Users/user-id/Views")
            {
                Assert.Contains("api_key=session-token", request.RequestUri.Query, StringComparison.OrdinalIgnoreCase);
                Assert.True(request.Headers.TryGetValues("X-Emby-Token", out var values) &&
                            Assert.Single(values) == "session-token");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "Items": [
                            { "Id": "movies", "Name": "Movies", "CollectionType": "movies" }
                          ]
                        }
                        """)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        })));
        var source = new MediaSource
        {
            Name = "Emby",
            ProtocolType = "emby",
            BaseUrl = "http://emby.local:8096",
            AuthConfig = MediaSourceAuthConfigSerializer.SerializeMediaServer(new MediaServerAuthConfig("password", "alice"))
        };

        var folders = await client.ListLibrariesAsync(source);

        var folder = Assert.Single(folders);
        Assert.Equal("Emby · Movies", folder.Name);
        var auth = MediaSourceAuthConfigSerializer.DeserializeMediaServer(folder.AuthConfig);
        Assert.Equal("session-token", auth?.Token);
        Assert.Equal("user-id", auth?.UserId);
        Assert.Equal("movies", auth?.LibraryId);
    }

    [Fact]
    public async Task MediaServerDiscoveryClient_AuthenticatesEmbyWithPasswordEvenWhenUsersEndpointIsPublic()
    {
        var client = new MediaServerDiscoveryClient(new HttpClient(new DelegateHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/Users/Me")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath == "/Users/AuthenticateByName")
            {
                var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
                Assert.Contains("\"Username\"", body, StringComparison.Ordinal);
                Assert.Contains("\"Pw\"", body, StringComparison.Ordinal);
                Assert.Contains("\"Password\"", body, StringComparison.Ordinal);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "AccessToken": "session-token",
                          "User": { "Id": "user-id", "Name": "alice" }
                        }
                        """)
                };
            }

            if (request.RequestUri?.AbsolutePath == "/Users")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""[{ "Id": "user-id", "Name": "alice" }]""")
                };
            }

            if (request.RequestUri?.AbsolutePath == "/Users/user-id/Views")
            {
                Assert.Contains("api_key=session-token", request.RequestUri.Query, StringComparison.OrdinalIgnoreCase);
                Assert.True(request.Headers.TryGetValues("X-Emby-Token", out var values) &&
                            Assert.Single(values) == "session-token");
                Assert.True(request.Headers.TryGetValues("X-MediaBrowser-Token", out var mediaBrowserValues) &&
                            Assert.Single(mediaBrowserValues) == "session-token");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "Items": [
                            { "Id": "movies", "Name": "Movies", "CollectionType": "movies" }
                          ]
                        }
                        """)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        })));
        var source = new MediaSource
        {
            Name = "Emby",
            ProtocolType = "emby",
            BaseUrl = "http://emby.local:8096",
            AuthConfig = MediaSourceAuthConfigSerializer.SerializeMediaServer(new MediaServerAuthConfig("password", "alice"))
        };

        var folders = await client.ListLibrariesAsync(source);

        var folder = Assert.Single(folders);
        Assert.Equal("Emby · Movies", folder.Name);
        var auth = MediaSourceAuthConfigSerializer.DeserializeMediaServer(folder.AuthConfig);
        Assert.Equal("session-token", auth?.Token);
        Assert.Equal("user-id", auth?.UserId);
        Assert.Equal("movies", auth?.LibraryId);
    }

    [Fact]
    public async Task MediaServerDiscoveryClient_FallsBackToWholeLibraryWhenViewsAreForbiddenButItemsWork()
    {
        var itemsProbeRequests = 0;
        var client = new MediaServerDiscoveryClient(new HttpClient(new DelegateHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/Users/Me")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{ "Id": "user-id", "Name": "alice" }""")
                };
            }

            if (request.RequestUri?.AbsolutePath is "/Users/user-id/Views" or "/Library/VirtualFolders")
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            if (request.RequestUri?.AbsolutePath == "/Items")
            {
                itemsProbeRequests++;
                Assert.Contains("api_key=api-key", request.RequestUri.Query, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Limit=1", request.RequestUri.Query, StringComparison.OrdinalIgnoreCase);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{ "Items": [] }""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        })));
        var source = new MediaSource
        {
            Name = "Emby",
            ProtocolType = "emby",
            BaseUrl = "http://emby.local:8096",
            AuthConfig = MediaSourceAuthConfigSerializer.SerializeMediaServer(new MediaServerAuthConfig("api-key", "alice"))
        };

        var folders = await client.ListLibrariesAsync(source);

        Assert.True(itemsProbeRequests >= 1);
        var folder = Assert.Single(folders);
        Assert.Equal("Emby · 全部媒体库", folder.Name);
        var auth = MediaSourceAuthConfigSerializer.DeserializeMediaServer(folder.AuthConfig);
        Assert.Equal("api-key", auth?.Token);
        Assert.Equal("user-id", auth?.UserId);
        Assert.Equal(string.Empty, auth?.LibraryId);
    }

    [Fact]
    public async Task MediaServerDiscoveryClient_FallsBackToWholeLibraryWhenApiKeyCannotResolveEmbyUsername()
    {
        var virtualFolderRequests = 0;
        var client = new MediaServerDiscoveryClient(new HttpClient(new DelegateHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath is "/Users/Me" or "/Users" or "/Users/AuthenticateByName")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (request.RequestUri?.AbsolutePath == "/Library/VirtualFolders")
            {
                virtualFolderRequests++;
                Assert.Contains("api_key=api-key", request.RequestUri.Query, StringComparison.OrdinalIgnoreCase);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        [
                          { "ItemId": "movies", "Name": "Movies", "CollectionType": "movies" }
                        ]
                        """)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        })));
        var source = new MediaSource
        {
            Name = "Emby",
            ProtocolType = "emby",
            BaseUrl = "http://emby.local:8096",
            AuthConfig = MediaSourceAuthConfigSerializer.SerializeMediaServer(new MediaServerAuthConfig("api-key", "alice"))
        };

        var folders = await client.ListLibrariesAsync(source);

        Assert.True(virtualFolderRequests >= 1);
        var folder = Assert.Single(folders);
        var auth = MediaSourceAuthConfigSerializer.DeserializeMediaServer(folder.AuthConfig);
        Assert.Equal("api-key", auth?.Token);
        Assert.Equal(string.Empty, auth?.UserId);
        Assert.Equal("movies", auth?.LibraryId);
    }

    [Fact]
    public async Task MediaServerDiscoveryClient_ResolvesEmbyUsernameBeforeEnumeratingItems()
    {
        var client = new MediaServerDiscoveryClient(new HttpClient(new DelegateHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/Users/Me")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (request.RequestUri?.AbsolutePath == "/Users")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""[{ "Id": "user-id", "Name": "alice" }]""")
                };
            }

            if (request.RequestUri?.AbsolutePath == "/Users/user-id/Items")
            {
                Assert.Contains("api_key=api-key", request.RequestUri.Query, StringComparison.OrdinalIgnoreCase);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "Items": [
                            {
                              "Id": "movie1",
                              "Name": "Movie",
                              "Type": "Movie",
                              "Path": "/movies/Movie.2020.mkv",
                              "MediaSources": [
                                { "Id": "movie1", "Path": "/movies/Movie.2020.mkv", "Container": "mkv", "Size": 123 }
                              ]
                            }
                          ]
                        }
                        """)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        })));
        var source = new MediaSource
        {
            Name = "Emby",
            ProtocolType = "emby",
            BaseUrl = "http://emby.local:8096",
            AuthConfig = MediaSourceAuthConfigSerializer.SerializeMediaServer(new MediaServerAuthConfig("api-key", "alice"))
        };

        var files = await client.EnumerateFilesAsync(source);

        var file = Assert.Single(files);
        Assert.Equal("Movie.2020.mkv", file.FileName);
        Assert.Equal("Items/movie1/Download", file.RelativePath);
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
