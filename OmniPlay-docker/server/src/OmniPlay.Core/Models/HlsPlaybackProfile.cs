namespace OmniPlay.Core.Models;

public sealed record HlsPlaybackProfile(
    string Mode,
    bool TranscodeVideo,
    string QualityId,
    int? MaxHeight,
    int? VideoBitrateKbps,
    int AudioBitrateKbps,
    int? AudioTrackIndex,
    string SubtitleMode,
    string? ExternalSubtitlePath,
    int? EmbeddedSubtitleStreamIndex,
    string? EmbeddedSubtitleCodec,
    bool PreferHardwareAcceleration,
    string? HardwareEncoder,
    string? HardwareDecoder,
    string? HardwareAcceleration,
    bool ToneMapToSdr,
    string ToneMapMode)
{
    private const string PipelineVersion = "v7";

    public string CacheKey =>
        string.Join(
            "_",
            PipelineVersion,
            Mode,
            QualityId,
            AudioTrackIndex?.ToString() ?? "a",
            SubtitleMode,
            ExternalSubtitlePath is null ? "nosub" : Path.GetFileNameWithoutExtension(ExternalSubtitlePath),
            EmbeddedSubtitleStreamIndex?.ToString() ?? "noembsub",
            EmbeddedSubtitleCodec ?? "nosubcodec",
            HardwareEncoder ?? "sw",
            HardwareDecoder ?? "nodec",
            HardwareAcceleration ?? "noaccel",
            ToneMapToSdr ? ToneMapMode : "native");

    public static HlsPlaybackProfile Remux { get; } = CreateRemux();

    public static HlsPlaybackProfile Transcode { get; } = CreateTranscode("auto");

    public static HlsPlaybackProfile CreateRemux(int? audioTrackIndex = null)
    {
        return new(
            "remux",
            false,
            "original",
            null,
            null,
            160,
            audioTrackIndex,
            "off",
            null,
            null,
            null,
            false,
            null,
            null,
            null,
            false,
            "off");
    }

    public static HlsPlaybackProfile CreateTranscode(
        string qualityId,
        int? audioTrackIndex = null,
        string subtitleMode = "off",
        string? externalSubtitlePath = null,
        int? embeddedSubtitleStreamIndex = null,
        string? embeddedSubtitleCodec = null,
        string? hardwareEncoder = null,
        string? hardwareDecoder = null,
        string? hardwareAcceleration = null,
        bool toneMapToSdr = false,
        string? toneMapMode = null)
    {
        var quality = HlsPlaybackQuality.Resolve(qualityId);
        return new(
            "transcode",
            true,
            quality.Id,
            quality.MaxHeight,
            quality.VideoBitrateKbps,
            quality.AudioBitrateKbps,
            audioTrackIndex,
            subtitleMode,
            externalSubtitlePath,
            embeddedSubtitleStreamIndex,
            embeddedSubtitleCodec,
            hardwareEncoder is not null,
            hardwareEncoder,
            hardwareDecoder,
            hardwareAcceleration,
            toneMapToSdr,
            toneMapToSdr ? NormalizeToneMapMode(toneMapMode) : "off");
    }

    private static string NormalizeToneMapMode(string? mode)
    {
        return mode?.Trim().ToLowerInvariant() switch
        {
            "hardware" => "hardware",
            "dolby-vision" => "dolby-vision",
            "software" => "software",
            _ => "software"
        };
    }
}

public sealed record HlsPlaybackQuality(
    string Id,
    int? MaxHeight,
    int? VideoBitrateKbps,
    int AudioBitrateKbps)
{
    public static HlsPlaybackQuality Resolve(string? id)
    {
        return (id ?? "auto").Trim().ToLowerInvariant() switch
        {
            "1080p" => new("1080p", 1080, 6000, 192),
            "720p" => new("720p", 720, 3500, 160),
            "480p" => new("480p", 480, 1600, 128),
            "360p" => new("360p", 360, 900, 96),
            _ => new("auto", null, null, 160)
        };
    }
}
