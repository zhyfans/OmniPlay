namespace OmniPlay.Core.Models;

public sealed record PlayableVideoFile(
    string Id,
    string AbsolutePath,
    string FileName,
    string MediaKind,
    long? FileSizeBytes,
    double DurationSeconds,
    string? Container,
    string? VideoCodec,
    string? AudioCodec,
    string? SubtitleSummary);
