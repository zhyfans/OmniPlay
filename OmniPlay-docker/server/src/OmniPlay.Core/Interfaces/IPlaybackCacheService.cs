using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface IPlaybackCacheService
{
    Task<PlaybackCacheStatus?> GetStatusAsync(
        string videoFileId,
        CancellationToken cancellationToken = default);

    Task<PlaybackCacheStatus?> StartAsync(
        string videoFileId,
        CancellationToken cancellationToken = default);

    Task<PlaybackCacheStatus?> CancelAsync(
        string videoFileId,
        CancellationToken cancellationToken = default);

    Task<string?> GetCompletedCachedPathAsync(
        string videoFileId,
        CancellationToken cancellationToken = default);

    Task<string?> EnsureCachedAsync(
        string videoFileId,
        CancellationToken cancellationToken = default);
}
