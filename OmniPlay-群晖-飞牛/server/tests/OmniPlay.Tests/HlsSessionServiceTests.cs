using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.FileSystem;
using OmniPlay.Media;
using Xunit;

namespace OmniPlay.Tests;

public sealed class HlsSessionServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omniplay-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EnsureSessionReportsFfmpegStartupFailure()
    {
        var mediaPath = Path.Combine(root, "media", "sample.mkv");
        Touch(mediaPath);
        var paths = new StoragePaths(Path.Combine(root, "app"));
        paths.EnsureCreated();

        var service = new FfmpegHlsSessionService(
            paths,
            Path.Combine(root, "missing-ffmpeg"),
            TimeSpan.FromMilliseconds(50));
        var file = new PlayableVideoFile("vf_sample", mediaPath, "sample.mkv", "movie", 4, 0, null, null, null, null);

        var session = await service.EnsureSessionAsync(file, HlsPlaybackProfile.Remux);

        Assert.False(session.IsReady);
        Assert.False(session.IsRunning);
        Assert.Contains("FFmpeg", session.ErrorMessage);
    }

    [Fact]
    public void GetAssetReturnsKnownHlsFilesOnlyInsideSessionDirectory()
    {
        var paths = new StoragePaths(Path.Combine(root, "app"));
        paths.EnsureCreated();
        var service = new FfmpegHlsSessionService(paths, "ffmpeg");
        var sessionDirectory = Path.Combine(paths.TranscodeDirectory, "vf_sample");
        Directory.CreateDirectory(sessionDirectory);
        File.WriteAllText(Path.Combine(sessionDirectory, "index.m3u8"), "#EXTM3U");
        File.WriteAllBytes(Path.Combine(sessionDirectory, "segment_00000.ts"), [0, 1, 2, 3]);

        var manifest = service.GetAsset("vf_sample", "index.m3u8");
        var segment = service.GetAsset("vf_sample", "segment_00000.ts");
        var traversal = service.GetAsset("vf_sample", "../secret.ts");

        Assert.NotNull(manifest);
        Assert.Equal("application/vnd.apple.mpegurl", manifest.ContentType);
        Assert.False(manifest.EnableRangeProcessing);
        Assert.NotNull(segment);
        Assert.Equal("video/mp2t", segment.ContentType);
        Assert.True(segment.EnableRangeProcessing);
        Assert.Null(traversal);
    }

    [Fact]
    public async Task GetCapabilitiesReportsMissingFfmpeg()
    {
        var paths = new StoragePaths(Path.Combine(root, "app"));
        paths.EnsureCreated();
        var service = new FfmpegHlsSessionService(paths, Path.Combine(root, "missing-ffmpeg"));

        var capabilities = await service.GetCapabilitiesAsync();

        Assert.False(capabilities.IsAvailable);
        Assert.Empty(capabilities.HardwareEncoders);
        Assert.Null(capabilities.PreferredHardwareEncoder);
        Assert.Empty(capabilities.HardwareDecoders);
        Assert.Null(capabilities.PreferredHardwareDecoder);
        Assert.Empty(capabilities.HardwareAccelerators);
    }

    [Fact]
    public void PreviewCommandUsesSameFfmpegArgumentsWithoutStartingProcess()
    {
        var mediaPath = Path.Combine(root, "media", "sample file.mkv");
        Touch(mediaPath);
        var paths = new StoragePaths(Path.Combine(root, "preview"));
        var service = new FfmpegHlsSessionService(paths, "ffmpeg");
        var file = new PlayableVideoFile("vf_preview", mediaPath, "sample file.mkv", "movie", 4, 0, null, "hevc", "dts", null);

        var command = service.PreviewCommand(
            file,
            HlsPlaybackProfile.CreateTranscode("720p", audioTrackIndex: 1, hardwareEncoder: "h264_vaapi"));

        Assert.Contains("ffmpeg", command);
        Assert.Contains("-i", command);
        Assert.Contains("sample file.mkv", command);
        Assert.Contains("-map 0:a:1?", command);
        Assert.Contains("-vaapi_device /dev/dri/renderD128", command);
        Assert.Contains("-c:v h264_vaapi", command);
        Assert.Contains("format=nv12,hwupload", command);
        Assert.False(Directory.Exists(paths.TranscodeDirectory));
    }

    [Fact]
    public async Task EnsureSessionRejectsSoftwareVideoTranscode()
    {
        var mediaPath = Path.Combine(root, "media", "sample.mkv");
        Touch(mediaPath);
        var paths = new StoragePaths(Path.Combine(root, "software-block"));
        paths.EnsureCreated();
        var service = new FfmpegHlsSessionService(paths, "ffmpeg", TimeSpan.FromMilliseconds(50));
        var file = new PlayableVideoFile("vf_software", mediaPath, "sample.mkv", "movie", 4, 0, null, "hevc", "dts", null);

        var session = await service.EnsureSessionAsync(file, HlsPlaybackProfile.CreateTranscode("720p"));

        Assert.False(session.IsReady);
        Assert.False(session.IsRunning);
        Assert.Contains("禁止软件转码", session.ErrorMessage);
    }

    [Fact]
    public void CleanupCacheRemovesOldSessionDirectories()
    {
        var paths = new StoragePaths(Path.Combine(root, "app"));
        paths.EnsureCreated();
        var service = new FfmpegHlsSessionService(paths, "ffmpeg");
        var oldDirectory = Path.Combine(paths.TranscodeDirectory, "old-session");
        Directory.CreateDirectory(oldDirectory);
        File.WriteAllBytes(Path.Combine(oldDirectory, "segment_00000.ts"), [0, 1, 2, 3]);
        Directory.SetLastWriteTimeUtc(oldDirectory, DateTime.UtcNow.AddHours(-48));

        var summary = service.CleanupCache(TimeSpan.FromHours(24));

        Assert.Equal(1, summary.RemovedSessionCount);
        Assert.Equal(4, summary.RemovedBytes);
        Assert.False(Directory.Exists(oldDirectory));
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
}
