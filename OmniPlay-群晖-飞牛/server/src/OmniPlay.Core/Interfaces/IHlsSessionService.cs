using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface IHlsSessionService
{
    Task<HlsPlaybackSession> EnsureSessionAsync(
        PlayableVideoFile file,
        HlsPlaybackProfile profile,
        CancellationToken cancellationToken = default);

    HlsPlaybackAsset? GetAsset(string sessionId, string assetName);

    Task<FfmpegTranscodeCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    string PreviewCommand(PlayableVideoFile file, HlsPlaybackProfile profile);

    bool StopSession(string sessionId);

    HlsCacheCleanupSummary CleanupCache(TimeSpan maxAge);
}
