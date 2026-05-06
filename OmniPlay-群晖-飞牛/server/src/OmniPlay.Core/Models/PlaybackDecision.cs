namespace OmniPlay.Core.Models;

public sealed record PlaybackDecision(
    string FileId,
    string Mode,
    string? StreamUrl,
    string? ManifestUrl,
    string? SessionId,
    bool IsReady,
    string? Reason);
