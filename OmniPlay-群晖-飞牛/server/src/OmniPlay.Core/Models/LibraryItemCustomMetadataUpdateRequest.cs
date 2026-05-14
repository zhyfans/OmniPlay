namespace OmniPlay.Core.Models;

public sealed record LibraryItemCustomMetadataUpdateRequest(
    string LibraryItemId,
    string Title,
    string? ReleaseDate,
    string? Overview,
    double? VoteAverage,
    string? PosterLocalPath = null,
    string? PosterRemotePath = null,
    bool LockMetadata = true);
