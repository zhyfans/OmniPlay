namespace OmniPlay.Core.Models;

public sealed record TmdbMetadataMatch(
    int Id,
    string MediaType,
    string Title,
    string? Overview,
    string? ReleaseDate,
    string? PosterPath,
    double? VoteAverage,
    double? Popularity);

public sealed record TmdbSeasonDetail(
    int SeasonNumber,
    string? Title,
    string? Overview,
    string? AirDate,
    string? PosterPath,
    IReadOnlyList<TmdbEpisodeDetail> Episodes);

public sealed record TmdbEpisodeDetail(
    int EpisodeNumber,
    string? Title,
    string? Overview,
    string? AirDate,
    string? StillPath);
