namespace OmniPlay.Core.Models;

public sealed record MediaSourceSummary(
    long Id,
    string Name,
    string Kind,
    string BaseUrl,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastScannedAt);
