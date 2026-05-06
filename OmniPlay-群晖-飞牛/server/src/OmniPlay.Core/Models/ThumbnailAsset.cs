namespace OmniPlay.Core.Models;

public sealed record ThumbnailAsset(
    string Id,
    string? VideoFileId,
    string LocalPath,
    int? Width,
    int? Height,
    DateTimeOffset CreatedAt);
