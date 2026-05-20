using System.Net;
using System.Net.Http.Headers;
using System.Text;
using OmniPlay.Infrastructure.FileSystem;
using Xunit;

namespace OmniPlay.Tests;

public sealed class WebDavDirectoryBrowserTests
{
    [Fact]
    public async Task TestConnectionSendsDepthZeroPropFindWithBasicAuth()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.MultiStatus));
        var browser = new WebDavDirectoryBrowser(new HttpClient(handler));

        var result = await browser.TestConnectionAsync("https://example.com/dav", "user", "secret");

        Assert.True(result.IsReachable);
        Assert.Equal("https://example.com/dav", result.Url);
        Assert.Equal("PROPFIND", handler.LastRequest?.Method.Method);
        Assert.Equal("0", handler.LastRequest?.Headers.GetValues("Depth").Single());
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("user:secret")),
            handler.LastRequest?.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task BrowseListsOnlyReadableCollections()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8" ?>
            <D:multistatus xmlns:D="DAV:">
              <D:response>
                <D:href>/dav/</D:href>
                <D:propstat>
                  <D:prop><D:resourcetype><D:collection /></D:resourcetype></D:prop>
                </D:propstat>
              </D:response>
              <D:response>
                <D:href>/dav/Movies/</D:href>
                <D:propstat>
                  <D:prop>
                    <D:displayname>Movies</D:displayname>
                    <D:resourcetype><D:collection /></D:resourcetype>
                    <D:getlastmodified>Sun, 03 May 2026 08:00:00 GMT</D:getlastmodified>
                  </D:prop>
                </D:propstat>
              </D:response>
              <D:response>
                <D:href>/dav/video.mp4</D:href>
                <D:propstat><D:prop><D:resourcetype /></D:prop></D:propstat>
              </D:response>
            </D:multistatus>
            """;
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml")
        });
        var browser = new WebDavDirectoryBrowser(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.com")
        });

        var result = await browser.BrowseAsync("https://example.com/dav", null, null);

        Assert.Equal("https://example.com/dav", result.CurrentUrl);
        Assert.Equal("https://example.com", result.ParentUrl);
        var entry = Assert.Single(result.Entries);
        Assert.Equal("Movies", entry.Name);
        Assert.Equal("https://example.com/dav/Movies", entry.Url);
        Assert.True(entry.IsReadable);
        Assert.NotNull(entry.LastModified);
        Assert.Equal("1", handler.LastRequest?.Headers.GetValues("Depth").Single());
    }

    [Fact]
    public async Task BrowseRejectsUnauthorizedResponse()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var browser = new WebDavDirectoryBrowser(new HttpClient(handler));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            browser.BrowseAsync("https://example.com/dav", "bad", "bad"));
    }

    [Fact]
    public async Task EnumerateFilesRecursesDirectoriesAndKeepsRelativePaths()
    {
        const string rootXml = """
            <?xml version="1.0" encoding="utf-8" ?>
            <D:multistatus xmlns:D="DAV:">
              <D:response>
                <D:href>/dav/</D:href>
                <D:propstat><D:prop><D:resourcetype><D:collection /></D:resourcetype></D:prop></D:propstat>
              </D:response>
              <D:response>
                <D:href>/dav/Movies/</D:href>
                <D:propstat><D:prop><D:displayname>Movies</D:displayname><D:resourcetype><D:collection /></D:resourcetype></D:prop></D:propstat>
              </D:response>
              <D:response>
                <D:href>/dav/readme.txt</D:href>
                <D:propstat><D:prop><D:displayname>readme.txt</D:displayname><D:resourcetype /><D:getcontentlength>12</D:getcontentlength></D:prop></D:propstat>
              </D:response>
            </D:multistatus>
            """;
        const string moviesXml = """
            <?xml version="1.0" encoding="utf-8" ?>
            <D:multistatus xmlns:D="DAV:">
              <D:response>
                <D:href>/dav/Movies/</D:href>
                <D:propstat><D:prop><D:resourcetype><D:collection /></D:resourcetype></D:prop></D:propstat>
              </D:response>
              <D:response>
                <D:href>/dav/Movies/Inception.2010.mkv</D:href>
                <D:propstat>
                  <D:prop>
                    <D:displayname>Inception.2010.mkv</D:displayname>
                    <D:resourcetype />
                    <D:getcontentlength>1024</D:getcontentlength>
                    <D:getlastmodified>Sun, 03 May 2026 09:00:00 GMT</D:getlastmodified>
                  </D:prop>
                </D:propstat>
              </D:response>
            </D:multistatus>
            """;
        var handler = new StubHttpMessageHandler(request => new HttpResponseMessage(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent(
                request.RequestUri?.AbsolutePath == "/dav/Movies/" ? moviesXml : rootXml,
                Encoding.UTF8,
                "application/xml")
        });
        var browser = new WebDavDirectoryBrowser(new HttpClient(handler));

        var files = await browser.EnumerateFilesAsync("https://example.com/dav", null, null);

        Assert.Equal(2, files.Count);
        Assert.Contains(files, file => file.RelativePath == "readme.txt" && file.ContentLength == 12);
        Assert.Contains(files, file => file.RelativePath == "Movies/Inception.2010.mkv" && file.ContentLength == 1024);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> handle;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handle)
        {
            this.handle = handle;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(handle(request));
        }
    }
}
