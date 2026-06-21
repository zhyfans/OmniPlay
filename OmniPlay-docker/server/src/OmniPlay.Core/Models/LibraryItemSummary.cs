namespace OmniPlay.Core.Models;

public sealed record LibraryItemSummary(
    string Id,
    string ItemKind,
    string Title,
    string? ReleaseDate,
    string? Overview,
    string? PosterAssetId,
    double? VoteAverage,
    double? DoubanRating,
    bool IsLocked,
    bool IsWatched,
    int VideoFileCount,
    double MaxProgressSeconds,
    double MaxDurationSeconds,
    DateTimeOffset UpdatedAt);
