namespace OmniPlay.Core.Models;

public sealed record PosterAsset(
    string Id,
    string? RemotePath,
    string LocalPath,
    int? Width,
    int? Height,
    DateTimeOffset CreatedAt);

