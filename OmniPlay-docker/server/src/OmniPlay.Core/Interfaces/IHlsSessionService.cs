using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface IHlsSessionService
{
    Task<HlsPlaybackSession> EnsureSessionAsync(
        PlayableVideoFile file,
        HlsPlaybackProfile profile,
        CancellationToken cancellationToken = default);

    Task<HlsPlaybackSession> EnsureCompletedSessionAsync(
        PlayableVideoFile file,
        HlsPlaybackProfile profile,
        IProgress<BackgroundTaskProgress>? progress = null,
        CancellationToken cancellationToken = default);

    HlsPlaybackSession? GetCompletedSession(
        PlayableVideoFile file,
        HlsPlaybackProfile profile);

    HlsPlaybackAsset? GetAsset(string sessionId, string assetName);

    Task<FfmpegTranscodeCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    string PreviewCommand(PlayableVideoFile file, HlsPlaybackProfile profile);

    bool StopSession(string sessionId);

    HlsCacheCleanupSummary CleanupCache(TimeSpan maxAge, long? maxBytes = null);
}
