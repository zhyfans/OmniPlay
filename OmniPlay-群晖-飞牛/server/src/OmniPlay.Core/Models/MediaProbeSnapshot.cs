namespace OmniPlay.Core.Models;

public sealed record MediaProbeSnapshot(
    string FilePath,
    double DurationSeconds,
    string? Container,
    string? VideoCodec,
    string? AudioCodec,
    string? SubtitleSummary,
    string? RawJson,
    IReadOnlyList<MediaStreamSnapshot> Streams);

public sealed record MediaStreamSnapshot(
    int Index,
    string Kind,
    string? Codec,
    string? Language,
    string? Title,
    int? Channels,
    string? ChannelLayout,
    bool IsDefault,
    bool IsForced);
