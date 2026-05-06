namespace OmniPlay.Core.Models;

public sealed record VideoFileProbeUpdate(
    string VideoFileId,
    double DurationSeconds,
    string? Container,
    string? VideoCodec,
    string? AudioCodec,
    string? SubtitleSummary,
    string? ProbeJson);
