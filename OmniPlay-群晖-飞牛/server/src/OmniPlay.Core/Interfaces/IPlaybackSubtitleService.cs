using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface IPlaybackSubtitleService
{
    Task<IReadOnlyList<PlaybackSubtitleTrack>?> DiscoverAsync(
        string videoFileId,
        CancellationToken cancellationToken = default);

    Task<string?> ResolveSubtitlePathAsync(
        string videoFileId,
        string? subtitleId,
        CancellationToken cancellationToken = default);
}
