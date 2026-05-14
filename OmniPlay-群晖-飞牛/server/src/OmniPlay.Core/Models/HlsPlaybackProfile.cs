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
    bool PreferHardwareAcceleration,
    string? HardwareEncoder,
    string? HardwareDecoder,
    string? HardwareAcceleration)
{
    public string CacheKey =>
        string.Join(
            "_",
            Mode,
            QualityId,
            AudioTrackIndex?.ToString() ?? "a",
            SubtitleMode,
            ExternalSubtitlePath is null ? "nosub" : Path.GetFileNameWithoutExtension(ExternalSubtitlePath),
            EmbeddedSubtitleStreamIndex?.ToString() ?? "noembsub",
            HardwareEncoder ?? "sw",
            HardwareDecoder ?? "nodec",
            HardwareAcceleration ?? "noaccel");

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
            false,
            null,
            null,
            null);
    }

    public static HlsPlaybackProfile CreateTranscode(
        string qualityId,
        int? audioTrackIndex = null,
        string subtitleMode = "off",
        string? externalSubtitlePath = null,
        int? embeddedSubtitleStreamIndex = null,
        string? hardwareEncoder = null,
        string? hardwareDecoder = null,
        string? hardwareAcceleration = null)
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
            hardwareEncoder is not null,
            hardwareEncoder,
            hardwareDecoder,
            hardwareAcceleration);
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
