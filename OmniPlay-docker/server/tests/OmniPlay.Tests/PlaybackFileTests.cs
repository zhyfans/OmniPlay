using System.Net;
using System.Net.Http.Headers;
using System.Text;
using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.FileSystem;
using OmniPlay.Infrastructure.Library;
using Xunit;

namespace OmniPlay.Tests;

public sealed class PlaybackFileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omniplay-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetPlayableVideoFileReturnsExistingLocalFile()
    {
        var mediaRoot = Path.Combine(root, "media");
        var moviePath = Path.Combine(mediaRoot, "Movies", "Sample (2026)", "Sample.2026.mp4");
        Touch(moviePath);

        var database = CreateDatabase();
        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        await new LibraryScanner(database).ScanAllAsync();

        var repository = new LibraryRepository(database);
        var item = (await repository.GetItemsAsync()).Single();
        var detail = await repository.GetItemDetailAsync(item.Id);
        Assert.NotNull(detail);

        var playable = await repository.GetPlayableVideoFileAsync(detail.VideoFiles[0].Id);

        Assert.NotNull(playable);
        Assert.Equal(Path.GetFullPath(moviePath), playable.AbsolutePath);
        Assert.Equal("Sample.2026.mp4", playable.FileName);
        Assert.Equal(4, playable.FileSizeBytes);
    }

    [Fact]
    public async Task GetPlayableVideoFileRejectsPathsOutsideMediaSource()
    {
        var mediaRoot = Path.Combine(root, "media");
        Directory.CreateDirectory(mediaRoot);
        Touch(Path.Combine(root, "outside.mp4"));

        var database = CreateDatabase();
        var source = await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        InsertVideoFile(database, source.Id, "../outside.mp4");

        var playable = await new LibraryRepository(database).GetPlayableVideoFileAsync("vf-traversal");

        Assert.Null(playable);
    }

    [Fact]
    public async Task PlayableFileResolverDownloadsWebDavFileToLocalCache()
    {
        var paths = new StoragePaths(Path.Combine(root, "app"));
        var database = new SqliteDatabase(paths);
        database.EnsureInitialized();
        var source = await new MediaSourceRepository(database).AddWebDavAsync(
            "远程媒体",
            "https://example.com/dav",
            "user",
            "secret");
        InsertVideoFile(
            database,
            source.Id,
            "Movies/Sample 2026.mp4",
            "Sample 2026.mp4",
            fileSizeBytes: 5,
            id: "vf-webdav");
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3, 4, 5])
        });
        var resolver = new PlayableFileResolver(database, paths, new HttpClient(handler));

        var playable = await resolver.ResolveAsync("vf-webdav");
        var cachedAgain = await resolver.ResolveAsync("vf-webdav");

        Assert.NotNull(playable);
        Assert.Equal("Sample 2026.mp4", playable.FileName);
        Assert.True(File.Exists(playable.AbsolutePath));
        Assert.StartsWith(Path.Combine(paths.CacheDirectory, "webdav"), playable.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal([1, 2, 3, 4, 5], await File.ReadAllBytesAsync(playable.AbsolutePath));
        Assert.Equal("https://example.com/dav/Movies/Sample%202026.mp4", handler.LastRequest?.RequestUri?.AbsoluteUri);
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("user:secret")),
            handler.LastRequest?.Headers.Authorization?.Parameter);
        Assert.Equal(playable.AbsolutePath, cachedAgain?.AbsolutePath);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task PlaybackCacheServicePreparesWebDavCacheAndReusesCachedFile()
    {
        var paths = new StoragePaths(Path.Combine(root, "app"));
        var database = new SqliteDatabase(paths);
        database.EnsureInitialized();
        var source = await new MediaSourceRepository(database).AddWebDavAsync(
            "远程媒体",
            "https://example.com/dav",
            "user",
            "secret");
        InsertVideoFile(
            database,
            source.Id,
            "Movies/Sample 2026.mp4",
            "Sample 2026.mp4",
            fileSizeBytes: 5,
            id: "vf-webdav-cache");
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3, 4, 5])
        });
        var service = new WebDavPlaybackCacheService(database, paths, new HttpClient(handler));

        var pending = await service.GetStatusAsync("vf-webdav-cache");
        var started = await service.StartAsync("vf-webdav-cache");
        var ready = await WaitForCacheStatusAsync(service, "vf-webdav-cache", static status => status.IsReady);
        var reused = await service.StartAsync("vf-webdav-cache");

        Assert.NotNull(pending);
        Assert.True(pending.CanStreamDirect);
        Assert.NotNull(started);
        Assert.NotNull(ready);
        Assert.True(ready.IsRemote);
        Assert.True(ready.IsReady);
        Assert.Equal(100, ready.Percent);
        Assert.Equal(5, ready.DownloadedBytes);
        Assert.NotNull(reused);
        Assert.True(reused.IsReady);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task PlaybackSubtitleServiceDiscoversAndCachesWebDavSiblingSubtitles()
    {
        var paths = new StoragePaths(Path.Combine(root, "app"));
        var database = new SqliteDatabase(paths);
        database.EnsureInitialized();
        var source = await new MediaSourceRepository(database).AddWebDavAsync(
            "远程媒体",
            "https://example.com/dav",
            "user",
            "secret");
        InsertVideoFile(
            database,
            source.Id,
            "Movies/Sample 2026.mkv",
            "Sample 2026.mkv",
            fileSizeBytes: 5,
            id: "vf-webdav-subtitles");

        var subtitleBytes = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\n你好\n");
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.Method.Method == "PROPFIND")
            {
                return new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent($$"""
                        <?xml version="1.0" encoding="utf-8"?>
                        <D:multistatus xmlns:D="DAV:">
                          <D:response>
                            <D:href>/dav/Movies/</D:href>
                            <D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop></D:propstat>
                          </D:response>
                          <D:response>
                            <D:href>/dav/Movies/Sample%202026.zh.srt</D:href>
                            <D:propstat><D:prop>
                              <D:displayname>Sample 2026.zh.srt</D:displayname>
                              <D:getcontentlength>{{subtitleBytes.Length}}</D:getcontentlength>
                              <D:resourcetype/>
                            </D:prop></D:propstat>
                          </D:response>
                          <D:response>
                            <D:href>/dav/Movies/Sample%202026.ass</D:href>
                            <D:propstat><D:prop>
                              <D:displayname>Sample 2026.ass</D:displayname>
                              <D:getcontentlength>9</D:getcontentlength>
                              <D:resourcetype/>
                            </D:prop></D:propstat>
                          </D:response>
                          <D:response>
                            <D:href>/dav/Movies/Other.srt</D:href>
                            <D:propstat><D:prop>
                              <D:displayname>Other.srt</D:displayname>
                              <D:getcontentlength>9</D:getcontentlength>
                              <D:resourcetype/>
                            </D:prop></D:propstat>
                          </D:response>
                        </D:multistatus>
                        """)
                };
            }

            if (request.Method == HttpMethod.Get
                && request.RequestUri?.AbsoluteUri == "https://example.com/dav/Movies/Sample%202026.zh.srt")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(subtitleBytes)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var service = new PlaybackSubtitleService(database, paths, new HttpClient(handler));

        var subtitles = await service.DiscoverAsync("vf-webdav-subtitles");
        Assert.NotNull(subtitles);
        var subtitleList = subtitles!;
        var selected = Assert.Single(subtitleList, subtitle => subtitle.Format == "srt");
        var cachedPath = await service.ResolveSubtitlePathAsync("vf-webdav-subtitles", selected.Id);
        Assert.NotNull(cachedPath);
        var subtitleCachePath = cachedPath!;
        var reusedPath = await service.ResolveSubtitlePathAsync("vf-webdav-subtitles", selected.Id);

        Assert.Equal(2, subtitleList.Count);
        Assert.Equal("Sample 2026.zh.srt", selected.FileName);
        Assert.Equal("zh", selected.Language);
        Assert.NotNull(selected.WebVttUrl);
        Assert.True(selected.CanBurn);
        Assert.Equal(subtitleCachePath, reusedPath);
        Assert.StartsWith(Path.Combine(paths.CacheDirectory, "webdav", "subtitles"), subtitleCachePath, StringComparison.Ordinal);
        Assert.Equal(subtitleBytes, await File.ReadAllBytesAsync(subtitleCachePath));
        Assert.Equal(1, handler.Requests.Count(request => request.Method == HttpMethod.Get));
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(
                Convert.ToBase64String(Encoding.UTF8.GetBytes("user:secret")),
                request.Headers.Authorization?.Parameter);
        });
    }

    [Fact]
    public async Task PlaybackSubtitleServiceDiscoversSingleVideoFolderSubtitlesWithoutExactPrefix()
    {
        var mediaRoot = Path.Combine(root, "single-video-subtitles");
        var videoPath = Path.Combine(mediaRoot, "Movies", "Sample (2026)", "Sample.Movie.2026.mkv");
        var subtitlePath = Path.Combine(mediaRoot, "Movies", "Sample (2026)", "Sample.zh.srt");
        Touch(videoPath);
        Touch(subtitlePath);

        var database = CreateDatabase();
        var source = await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        InsertVideoFile(
            database,
            source.Id,
            "Movies/Sample (2026)/Sample.Movie.2026.mkv",
            "Sample.Movie.2026.mkv",
            fileSizeBytes: 4,
            id: "vf-single-folder-subtitles");
        var service = new PlaybackSubtitleService(database, new StoragePaths(Path.Combine(root, "app")), new HttpClient());

        var subtitles = await service.DiscoverAsync("vf-single-folder-subtitles");

        Assert.NotNull(subtitles);
        var subtitle = Assert.Single(subtitles!);
        Assert.Equal("Sample.zh.srt", subtitle.FileName);
        Assert.Equal("zh", subtitle.Language);
    }

    [Fact]
    public async Task PlaybackSubtitleServiceMatchesEpisodeSubtitleWithoutFullReleaseName()
    {
        var mediaRoot = Path.Combine(root, "episode-subtitles");
        var episode1 = Path.Combine(mediaRoot, "Shows", "Sample", "Season 1", "Sample.S01E01.Release.mkv");
        var episode2 = Path.Combine(mediaRoot, "Shows", "Sample", "Season 1", "Sample.S01E02.Release.mkv");
        Touch(episode1);
        Touch(episode2);
        Touch(Path.Combine(mediaRoot, "Shows", "Sample", "Season 1", "Sample.S01E01.zh.srt"));
        Touch(Path.Combine(mediaRoot, "Shows", "Sample", "Season 1", "Sample.S01E02.zh.srt"));

        var database = CreateDatabase();
        var source = await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        InsertVideoFile(
            database,
            source.Id,
            "Shows/Sample/Season 1/Sample.S01E01.Release.mkv",
            "Sample.S01E01.Release.mkv",
            fileSizeBytes: 4,
            id: "vf-episode-subtitles");
        var service = new PlaybackSubtitleService(database, new StoragePaths(Path.Combine(root, "app")), new HttpClient());

        var subtitles = await service.DiscoverAsync("vf-episode-subtitles");

        Assert.NotNull(subtitles);
        var subtitle = Assert.Single(subtitles!);
        Assert.Equal("Sample.S01E01.zh.srt", subtitle.FileName);
        Assert.Equal("zh", subtitle.Language);
    }

    [Fact]
    public async Task WebDavRangeStreamServiceCachesRequestedSegments()
    {
        var paths = new StoragePaths(Path.Combine(root, "app"));
        var database = new SqliteDatabase(paths);
        database.EnsureInitialized();
        var source = await new MediaSourceRepository(database).AddWebDavAsync(
            "远程媒体",
            "https://example.com/dav",
            "user",
            "secret");
        InsertVideoFile(
            database,
            source.Id,
            "Movies/Sample 2026.mp4",
            "Sample 2026.mp4",
            fileSizeBytes: 12,
            id: "vf-webdav-range");
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal("bytes=0-11", request.Headers.Range?.ToString());
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11])
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 11, 12);
            return response;
        });
        var service = new WebDavRangeStreamService(database, paths, new HttpClient(handler));

        var first = await service.OpenReadAsync("vf-webdav-range", "bytes=2-5");
        Assert.NotNull(first);
        byte[] firstBytes;
        await using (var firstResult = first!)
        {
            Assert.Equal(206, firstResult.StatusCode);
            Assert.Equal("bytes 2-5/12", firstResult.ContentRange);
            Assert.Equal(4, firstResult.ContentLength);
            firstBytes = await ReadAllBytesAsync(firstResult.Content!);
        }

        var second = await service.OpenReadAsync("vf-webdav-range", "bytes=4-7");
        Assert.NotNull(second);
        byte[] secondBytes;
        await using (var secondResult = second!)
        {
            secondBytes = await ReadAllBytesAsync(secondResult.Content!);
        }

        Assert.Equal([2, 3, 4, 5], firstBytes);
        Assert.Equal([4, 5, 6, 7], secondBytes);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("https://example.com/dav/Movies/Sample%202026.mp4", handler.LastRequest?.RequestUri?.AbsoluteUri);
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("user:secret")),
            handler.LastRequest?.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task WebDavRangeStreamServicePassesThroughOpenEndedRange()
    {
        var paths = new StoragePaths(Path.Combine(root, "app-open-range"));
        var database = new SqliteDatabase(paths);
        database.EnsureInitialized();
        var source = await new MediaSourceRepository(database).AddWebDavAsync(
            "远程媒体",
            "https://example.com/dav",
            "user",
            "secret");
        InsertVideoFile(
            database,
            source.Id,
            "Movies/Sample 2026.mp4",
            "Sample 2026.mp4",
            fileSizeBytes: 20,
            id: "vf-webdav-open-range");
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal("bytes=0-", request.Headers.Range?.ToString());
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([
                    0, 1, 2, 3, 4, 5, 6, 7, 8, 9,
                    10, 11, 12, 13, 14, 15, 16, 17, 18, 19
                ])
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 19, 20);
            return response;
        });
        var service = new WebDavRangeStreamService(database, paths, new HttpClient(handler));

        var result = await service.OpenReadAsync("vf-webdav-open-range", "bytes=0-");

        Assert.NotNull(result);
        await using var streamResult = result!;
        Assert.Equal(206, streamResult.StatusCode);
        Assert.Equal("bytes 0-19/20", streamResult.ContentRange);
        Assert.Equal(20, streamResult.ContentLength);
        Assert.Equal(1, handler.RequestCount);
    }

    private static async Task<PlaybackCacheStatus> WaitForCacheStatusAsync(
        WebDavPlaybackCacheService service,
        string videoFileId,
        Func<PlaybackCacheStatus, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!timeout.IsCancellationRequested)
        {
            var status = await service.GetStatusAsync(videoFileId, timeout.Token);
            if (status is not null && predicate(status))
            {
                return status;
            }

            await Task.Delay(10, timeout.Token);
        }

        throw new TimeoutException("Timed out waiting for playback cache status.");
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    private SqliteDatabase CreateDatabase()
    {
        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();
        return database;
    }

    private static void InsertVideoFile(SqliteDatabase database, long sourceId, string relativePath)
    {
        InsertVideoFile(database, sourceId, relativePath, "outside.mp4", 4, "vf-traversal");
    }

    private static void InsertVideoFile(
        SqliteDatabase database,
        long sourceId,
        string relativePath,
        string fileName,
        long fileSizeBytes,
        string id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO video_files (
                id, source_id, library_item_id, episode_id, relative_path, file_name, file_size_bytes, modified_at,
                media_kind, duration_seconds, container, video_codec, audio_codec, subtitle_summary, probe_json,
                created_at, updated_at, missing_at)
            VALUES (
                $id, $sourceId, NULL, NULL, $relativePath, $fileName, $fileSizeBytes, NULL,
                'movie', 0, NULL, NULL, NULL, NULL, NULL, $now, $now, NULL);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$sourceId", sourceId);
        command.Parameters.AddWithValue("$relativePath", relativePath);
        command.Parameters.AddWithValue("$fileName", fileName);
        command.Parameters.AddWithValue("$fileSizeBytes", fileSizeBytes);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0, 1, 2, 3]);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> handle;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handle)
        {
            this.handle = handle;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        public int RequestCount { get; private set; }

        public IReadOnlyList<HttpRequestMessage> Requests => requests;

        private readonly List<HttpRequestMessage> requests = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequest = request;
            requests.Add(request);
            return Task.FromResult(handle(request));
        }
    }
}
