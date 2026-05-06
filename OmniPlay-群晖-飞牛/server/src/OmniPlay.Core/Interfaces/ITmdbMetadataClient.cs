using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface ITmdbMetadataClient
{
    Task<TmdbMetadataMatch?> SearchAsync(
        string mediaType,
        string title,
        string? year,
        TmdbSettings settings,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TmdbMetadataMatch>> SearchCandidatesAsync(
        string mediaType,
        string title,
        string? year,
        TmdbSettings settings,
        int limit = 8,
        CancellationToken cancellationToken = default);

    Task<TmdbMetadataMatch?> GetDetailsAsync(
        string mediaType,
        int tmdbId,
        TmdbSettings settings,
        CancellationToken cancellationToken = default);

    Task<TmdbSeasonDetail?> GetSeasonAsync(
        int tvTmdbId,
        int seasonNumber,
        TmdbSettings settings,
        CancellationToken cancellationToken = default);

    Task<string?> DownloadPosterAsync(
        string posterPath,
        string mediaType,
        int tmdbId,
        CancellationToken cancellationToken = default);

    Task<string?> DownloadStillAsync(
        string stillPath,
        int tvTmdbId,
        int seasonNumber,
        int episodeNumber,
        CancellationToken cancellationToken = default);
}
