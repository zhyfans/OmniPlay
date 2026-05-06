namespace OmniPlay.Core.Models;

public sealed record PlaybackSubtitleTrack(
    string Id,
    string FileName,
    string Format,
    string? Language,
    string? WebVttUrl,
    bool CanBurn);
