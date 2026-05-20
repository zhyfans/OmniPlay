namespace OmniPlay.Core.Models;

public sealed record MediaSourceCleanupSummary(
    int RemovedVideoFileCount,
    int RemovedPlaybackProgressCount,
    int RemovedThumbnailAssetRecordCount,
    int RemovedTranscodeJobCount,
    int RemovedEpisodeCount,
    int RemovedSeasonCount,
    int RemovedTvShowCount,
    int RemovedMovieCount,
    int RemovedLibraryItemCount);
