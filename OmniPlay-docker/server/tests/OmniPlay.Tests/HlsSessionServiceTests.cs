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
    public async Task EnsureSessionClearsStaleIncompleteManifestBeforeRestart()
    {
        var mediaPath = Path.Combine(root, "media", "sample.mkv");
        Touch(mediaPath);
        var paths = new StoragePaths(Path.Combine(root, "stale-manifest"));
        paths.EnsureCreated();
        var file = new PlayableVideoFile("vf_stale", mediaPath, "sample.mkv", "movie", 4, 0, null, null, null, null);
        var sessionDirectory = Path.Combine(paths.TranscodeDirectory, $"{file.Id}_{HlsPlaybackProfile.Remux.CacheKey}");
        Directory.CreateDirectory(sessionDirectory);
        var manifestPath = Path.Combine(sessionDirectory, "index.m3u8");
        var staleSegmentPath = Path.Combine(sessionDirectory, "segment_00000.m4s");
        File.WriteAllText(manifestPath, "#EXTM3U\n#EXT-X-TARGETDURATION:2\n");
        File.WriteAllBytes(staleSegmentPath, [0, 1, 2, 3]);
        var service = new FfmpegHlsSessionService(
            paths,
            Path.Combine(root, "missing-ffmpeg"),
            TimeSpan.FromMilliseconds(50));

        var session = await service.EnsureSessionAsync(file, HlsPlaybackProfile.Remux);

        Assert.False(session.IsReady);
        Assert.False(File.Exists(manifestPath));
        Assert.False(File.Exists(staleSegmentPath));
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
        File.WriteAllBytes(Path.Combine(sessionDirectory, "init.mp4"), [4, 5, 6]);
        File.WriteAllBytes(Path.Combine(sessionDirectory, "segment_00000.m4s"), [7, 8, 9]);

        var manifest = service.GetAsset("vf_sample", "index.m3u8");
        var transportSegment = service.GetAsset("vf_sample", "segment_00000.ts");
        var initSegment = service.GetAsset("vf_sample", "init.mp4");
        var mediaSegment = service.GetAsset("vf_sample", "segment_00000.m4s");
        var traversal = service.GetAsset("vf_sample", "../secret.ts");

        Assert.NotNull(manifest);
        Assert.Equal("application/vnd.apple.mpegurl", manifest.ContentType);
        Assert.False(manifest.EnableRangeProcessing);
        Assert.NotNull(transportSegment);
        Assert.Equal("video/mp2t", transportSegment.ContentType);
        Assert.True(transportSegment.EnableRangeProcessing);
        Assert.NotNull(initSegment);
        Assert.Equal("video/mp4", initSegment.ContentType);
        Assert.True(initSegment.EnableRangeProcessing);
        Assert.NotNull(mediaSegment);
        Assert.Equal("video/iso.segment", mediaSegment.ContentType);
        Assert.True(mediaSegment.EnableRangeProcessing);
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
        Assert.Contains("-init_hw_device vaapi=va:/dev/dri/renderD128", command);
        Assert.Contains("-filter_hw_device va", command);
        Assert.Contains("-c:v h264_vaapi", command);
        Assert.Contains("format=nv12,hwupload", command);
        Assert.Contains("-hls_playlist_type event", command);
        Assert.Contains("-hls_time 2", command);
        Assert.Contains("-hls_segment_type fmp4", command);
        Assert.Contains("-hls_fmp4_init_filename init.mp4", command);
        Assert.Contains("segment_%05d.m4s", command);
        Assert.False(Directory.Exists(paths.TranscodeDirectory));
    }

    [Fact]
    public void PreviewCommandConvertsVaapiDecodedFramesToNv12BeforeH264VaapiEncode()
    {
        var mediaPath = Path.Combine(root, "media", "hdr sample.mkv");
        Touch(mediaPath);
        var paths = new StoragePaths(Path.Combine(root, "vaapi-preview"));
        var service = new FfmpegHlsSessionService(paths, "ffmpeg");
        var file = new PlayableVideoFile("vf_hdr", mediaPath, "hdr sample.mkv", "movie", 4, 0, null, "hevc", "dts", null);

        var command = service.PreviewCommand(
            file,
            HlsPlaybackProfile.CreateTranscode(
                "auto",
                hardwareEncoder: "h264_vaapi",
                hardwareDecoder: "hevc_vaapi",
                hardwareAcceleration: "vaapi"));

        Assert.Contains("-hwaccel vaapi", command);
        Assert.Contains("-hwaccel_output_format vaapi", command);
        Assert.Contains("-vf hwdownload,format=nv12,format=nv12,hwupload", command);
        Assert.DoesNotContain("scale_vaapi", command);
        Assert.Contains("-c:v h264_vaapi", command);
    }

    [Fact]
    public void PreviewCommandReportsMissingHardwareEncoderWhenVideoTranscodeIsRequired()
    {
        var mediaPath = Path.Combine(root, "media", "sample.mkv");
        Touch(mediaPath);
        var paths = new StoragePaths(Path.Combine(root, "hardware-required-preview"));
        var service = new FfmpegHlsSessionService(paths, "ffmpeg");
        var file = new PlayableVideoFile("vf_software", mediaPath, "sample.mkv", "movie", 4, 0, null, "hevc", "dts", null);

        var command = service.PreviewCommand(file, HlsPlaybackProfile.CreateTranscode("720p"));

        Assert.Contains("没有检测到可用硬件编码器", command);
    }

    [Fact]
    public void PreviewCommandToneMapsHdrDolbyVisionToSdr()
    {
        var mediaPath = Path.Combine(root, "media", "The.Legend.of.Hei.2.2025.2160p.H265.DV.DTS.mkv");
        Touch(mediaPath);
        var paths = new StoragePaths(Path.Combine(root, "tone-map-preview"));
        var service = new FfmpegHlsSessionService(paths, "ffmpeg");
        var file = new PlayableVideoFile("vf_tonemap", mediaPath, Path.GetFileName(mediaPath), "movie", 4, 0, null, "hevc", "dts", null);

        var command = service.PreviewCommand(
            file,
            HlsPlaybackProfile.CreateTranscode(
                "1080p",
                hardwareEncoder: "h264_vaapi",
                hardwareDecoder: "hevc_vaapi",
                hardwareAcceleration: "vaapi",
                toneMapToSdr: true));

        Assert.Contains("-c:v h264_vaapi", command);
        Assert.Contains("-hwaccel vaapi", command);
        Assert.Contains("hwdownload,format=p010le,scale=-2:min(ih\\,1080),zscale=", command);
        Assert.Contains("format=nv12,hwupload", command);
        Assert.Contains("-color_primaries bt709", command);
        Assert.DoesNotContain("tonemap_vaapi", command);
        Assert.DoesNotContain("scale_vaapi", command);
        Assert.DoesNotContain("libx264", command);
    }

    [Fact]
    public void PreviewCommandToneMapsHdrWithBurnedSubtitleBeforeVaapiEncode()
    {
        var mediaPath = Path.Combine(root, "media", "The.Manipulated.S01E01.2025.2160p.H265.HDR.DV.mkv");
        Touch(mediaPath);
        var paths = new StoragePaths(Path.Combine(root, "tone-map-subtitle-preview"));
        var service = new FfmpegHlsSessionService(paths, "ffmpeg");
        var file = new PlayableVideoFile("vf_tonemap_subtitle", mediaPath, Path.GetFileName(mediaPath), "episode", 4, 0, null, "hevc", "eac3", null);

        var command = service.PreviewCommand(
            file,
            HlsPlaybackProfile.CreateTranscode(
                "1080p",
                subtitleMode: "burn",
                embeddedSubtitleStreamIndex: 0,
                hardwareEncoder: "h264_vaapi",
                hardwareDecoder: "hevc_vaapi",
                hardwareAcceleration: "vaapi",
                toneMapToSdr: true));

        Assert.Contains("-c:v h264_vaapi", command);
        Assert.Contains("-hwaccel vaapi", command);
        Assert.Contains("hwdownload,format=p010le,scale=-2:min(ih\\,1080),zscale=", command);
        Assert.Contains("scale=-2:min(ih\\,1080)", command);
        Assert.Contains("subtitles='", command);
        Assert.Contains(":si=0", command);
        Assert.Contains("format=nv12,hwupload", command);
        Assert.DoesNotContain("tonemap_vaapi", command);
        Assert.DoesNotContain("scale_vaapi", command);
        Assert.DoesNotContain("libx264", command);
    }

    [Fact]
    public void PreviewCommandBurnsPgsEmbeddedSubtitleWithOverlayFilterGraph()
    {
        var mediaPath = Path.Combine(root, "media", "BluRay.Sample.2160p.HEVC.PGS.m2ts");
        Touch(mediaPath);
        var paths = new StoragePaths(Path.Combine(root, "pgs-subtitle-preview"));
        var service = new FfmpegHlsSessionService(paths, "ffmpeg");
        var file = new PlayableVideoFile("vf_pgs_subtitle", mediaPath, Path.GetFileName(mediaPath), "movie", 4, 0, null, "hevc", "truehd", null);

        var command = service.PreviewCommand(
            file,
            HlsPlaybackProfile.CreateTranscode(
                "1080p",
                subtitleMode: "burn-bitmap",
                embeddedSubtitleStreamIndex: 0,
                embeddedSubtitleCodec: "hdmv_pgs_subtitle",
                hardwareEncoder: "h264_vaapi",
                hardwareDecoder: "hevc_vaapi",
                hardwareAcceleration: "vaapi"));

        Assert.Contains("-filter_complex", command);
        Assert.Contains("[vbase][0:s:0]overlay=eof_action=pass:shortest=0,format=nv12,hwupload[vout]", command);
        Assert.Contains("-map [vout]", command);
        Assert.DoesNotContain("subtitles='", command);
        Assert.Contains("-c:v h264_vaapi", command);
    }

    [Fact]
    public void PreviewCommandUsesDolbyVisionIctcpToneMap()
    {
        var mediaPath = Path.Combine(root, "media", "The.Legend.of.Hei.2.2025.2160p.H265.DV.DTS.mkv");
        Touch(mediaPath);
        var paths = new StoragePaths(Path.Combine(root, "dolby-vision-tone-map-preview"));
        var service = new FfmpegHlsSessionService(paths, "ffmpeg");
        var file = new PlayableVideoFile("vf_dv_tonemap", mediaPath, Path.GetFileName(mediaPath), "movie", 4, 0, null, "hevc", "dts", null);

        var command = service.PreviewCommand(
            file,
            HlsPlaybackProfile.CreateTranscode(
                "1080p",
                hardwareEncoder: "h264_vaapi",
                hardwareDecoder: "hevc_vaapi",
                hardwareAcceleration: "vaapi",
                toneMapToSdr: true,
                toneMapMode: "dolby-vision"));

        Assert.Contains("zscale=matrixin=ictcp:transferin=smpte2084:primariesin=bt2020:rangein=tv:matrix=gbr:transfer=linear:primaries=bt2020:npl=100", command);
        Assert.Contains("format=gbrpf32le,tonemap=tonemap=hable:desat=0,zscale=primaries=bt709:transfer=bt709:matrix=bt709:range=tv", command);
        Assert.DoesNotContain("colorspace=iall=bt2020", command);
        Assert.Contains("format=nv12,hwupload", command);
    }

    [Fact]
    public void PreviewCommandUsesVaapiToneMapWhenHardwareToneMapIsSelected()
    {
        var mediaPath = Path.Combine(root, "media", "Hdr10.Sample.2160p.HEVC.HDR10.mkv");
        Touch(mediaPath);
        var paths = new StoragePaths(Path.Combine(root, "hardware-tone-map-preview"));
        var service = new FfmpegHlsSessionService(paths, "ffmpeg");
        var file = new PlayableVideoFile("vf_hw_tonemap", mediaPath, Path.GetFileName(mediaPath), "movie", 4, 0, null, "hevc", "eac3", null);

        var command = service.PreviewCommand(
            file,
            HlsPlaybackProfile.CreateTranscode(
                "1080p",
                hardwareEncoder: "h264_vaapi",
                hardwareDecoder: "hevc_vaapi",
                hardwareAcceleration: "vaapi",
                toneMapToSdr: true,
                toneMapMode: "hardware"));

        Assert.Contains("tonemap_vaapi=format=nv12,scale_vaapi=w=-2:h=1080:format=nv12", command);
        Assert.Contains("-c:v h264_vaapi", command);
        Assert.DoesNotContain("hwdownload", command);
        Assert.DoesNotContain("libx264", command);
    }

    [Fact]
    public void PreviewCommandKeepsH264VideoCopyWhileTranscodingFlacAudioForHls()
    {
        var mediaPath = Path.Combine(root, "media", "Medalist.S01E01.2025.1080p.BluRay.Remux.AVC.FLAC.2.0.mkv");
        Touch(mediaPath);
        var paths = new StoragePaths(Path.Combine(root, "flac-remux-preview"));
        var service = new FfmpegHlsSessionService(paths, "ffmpeg");
        var file = new PlayableVideoFile("vf_flac", mediaPath, Path.GetFileName(mediaPath), "episode", 4, 0, null, "h264", "flac", null);

        var command = service.PreviewCommand(file, HlsPlaybackProfile.Remux);

        Assert.Contains("-c:v copy", command);
        Assert.Contains("-c:a aac", command);
        Assert.Contains("-hls_playlist_type event", command);
        Assert.Contains("-hls_segment_type fmp4", command);
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
