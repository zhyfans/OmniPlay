namespace OmniPlay.Core.Models;

public sealed record CacheSettings(
    int HlsRetentionHours = 24,
    string ImageCleanupScope = "orphans-and-untracked",
    int WebDavRetentionHours = 72,
    int WebDavMaxGb = 20);
