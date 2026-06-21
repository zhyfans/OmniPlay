using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.FileSystem;
using OmniPlay.Infrastructure.SystemChecks;
using Xunit;

namespace OmniPlay.Tests;

public sealed class RuntimeSelfCheckServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omniplay-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CheckReportsWritableRuntimeWithoutWebDavSection()
    {
        var paths = new StoragePaths(Path.Combine(root, "app"));
        var database = new SqliteDatabase(paths);
        database.EnsureInitialized();
        var service = new RuntimeSelfCheckService(
            paths,
            database,
            new StubHlsSessionService(),
            new HttpClient(new StubHttpMessageHandler()),
            new Uri("http://0.0.0.0:8096"));

        var snapshot = await service.CheckAsync();

        Assert.Equal("warn", snapshot.Status);
        Assert.Contains(snapshot.Items, item => item.Key == "cache-write" && item.Status == "ok");
        Assert.Contains(snapshot.Items, item => item.Key == "sqlite-write" && item.Status == "ok");
        Assert.Contains(snapshot.Items, item => item.Key == "ffmpeg" && item.Status == "ok");
        Assert.Contains(snapshot.Items, item => item.Key == "hardware-encoding" && item.Status == "ok");
        Assert.DoesNotContain(snapshot.Items, item => item.Key == "webdav-range");
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StubHlsSessionService : IHlsSessionService
    {
        public Task<HlsPlaybackSession> EnsureSessionAsync(
            PlayableVideoFile file,
            HlsPlaybackProfile profile,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new HlsPlaybackSession("stub", "", "", false, false, null));
        }

        public Task<HlsPlaybackSession> EnsureCompletedSessionAsync(
            PlayableVideoFile file,
            HlsPlaybackProfile profile,
            IProgress<BackgroundTaskProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return EnsureSessionAsync(file, profile, cancellationToken);
        }

        public HlsPlaybackSession? GetCompletedSession(
            PlayableVideoFile file,
            HlsPlaybackProfile profile)
        {
            return null;
        }

        public HlsPlaybackAsset? GetAsset(string sessionId, string assetName)
        {
            return null;
        }

        public Task<FfmpegTranscodeCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new FfmpegTranscodeCapabilities(
                true,
                "ffmpeg",
                ["h264_vaapi", "hevc_vaapi"],
                "h264_vaapi",
                ["h264_vaapi", "hevc_vaapi", "vp9_vaapi", "av1_vaapi", "mpeg2_vaapi"],
                "h264_vaapi",
                ["vaapi"],
                null,
                DateTimeOffset.UtcNow));
        }

        public string PreviewCommand(PlayableVideoFile file, HlsPlaybackProfile profile)
        {
            return "ffmpeg -i input";
        }

        public bool StopSession(string sessionId)
        {
            return false;
        }

        public HlsCacheCleanupSummary CleanupCache(TimeSpan maxAge, long? maxBytes = null)
        {
            return new HlsCacheCleanupSummary(0, 0);
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
