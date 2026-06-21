using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface ISubtitleCacheService
{
    Task<string?> ReadExternalSubtitleAsWebVttAsync(
        string subtitlePath,
        CancellationToken cancellationToken = default);

    Task<string?> ReadEmbeddedSubtitleAsWebVttAsync(
        string inputPath,
        int subtitleOrdinal,
        CancellationToken cancellationToken = default);

    Task<string?> ExtractEmbeddedSubtitleAsSupAsync(
        string inputPath,
        int subtitleOrdinal,
        CancellationToken cancellationToken = default);

    Task<SubtitleCacheStatus> GetPgsCacheStatusAsync(
        string videoFileId,
        string? inputPath = null,
        CancellationToken cancellationToken = default);

    Task<SubtitleCachePrewarmSummary> PrewarmLibraryAsync(
        string? targetLibraryItemId = null,
        IProgress<BackgroundTaskProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<SubtitleCachePrewarmSummary> PrewarmNextEpisodeAsync(
        string videoFileId,
        CancellationToken cancellationToken = default);

    Task<SubtitleCacheCleanupSummary> CleanupAsync(
        long? maxBytes = null,
        CancellationToken cancellationToken = default);
}
