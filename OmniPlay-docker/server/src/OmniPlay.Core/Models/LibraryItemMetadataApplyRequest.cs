namespace OmniPlay.Core.Models;

public sealed record LibraryItemMetadataApplyRequest(
    string LibraryItemId,
    int TmdbId,
    string MediaType,
    string Title,
    string? Overview,
    string? ReleaseDate,
    string? PosterPath,
    double? VoteAverage,
    string? PosterLocalPath,
    bool LockMetadata = true);
