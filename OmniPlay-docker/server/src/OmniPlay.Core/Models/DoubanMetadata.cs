namespace OmniPlay.Core.Models;

public sealed record DoubanMetadata(
    string LibraryItemId,
    string SubjectId,
    string SubjectUrl,
    string Title,
    string? OriginalTitle,
    string? Year,
    double? Rating,
    int? RatingCount,
    string? Summary,
    string? Genres,
    string? Countries,
    string? PosterUrl,
    DateTimeOffset FetchedAt);

public sealed record DoubanBindRequest(string Subject);

public sealed record DoubanMetadataImportRequest(
    string SubjectId,
    string SubjectUrl,
    string Title,
    string? OriginalTitle,
    string? Year,
    double? Rating,
    int? RatingCount,
    string? Summary,
    string? Genres,
    string? Countries,
    string? PosterUrl,
    DateTimeOffset? FetchedAt);
