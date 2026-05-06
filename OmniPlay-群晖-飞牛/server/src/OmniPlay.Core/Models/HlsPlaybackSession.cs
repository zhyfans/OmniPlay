namespace OmniPlay.Core.Models;

public sealed record HlsPlaybackSession(
    string SessionId,
    string ManifestPath,
    string OutputDirectory,
    bool IsReady,
    bool IsRunning,
    string? ErrorMessage);
