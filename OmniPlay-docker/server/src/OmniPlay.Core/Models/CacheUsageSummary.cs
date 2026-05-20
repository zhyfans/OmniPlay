namespace OmniPlay.Core.Models;

public sealed record CacheUsageSummary(
    long TotalBytes,
    int TotalFileCount,
    IReadOnlyList<CacheUsageBucket> Buckets,
    DateTimeOffset UpdatedAt);

public sealed record CacheUsageBucket(
    string Key,
    string Label,
    string Path,
    long Bytes,
    int FileCount);
