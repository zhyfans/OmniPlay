namespace OmniPlay.Core.Models;

public sealed record CacheSettings(
    int HlsRetentionHours = 24,
    int HlsMaxGb = 30,
    string HlsCachePath = "",
    string ImageCleanupScope = "orphans-and-untracked",
    int WebDavRetentionHours = 72,
    int WebDavMaxGb = 20,
    string SubtitleCachePath = "",
    int SubtitleMaxGb = 20,
    string SubtitleCacheStrategy = SubtitleCacheStrategies.Optimized);

public static class SubtitleCacheStrategies
{
    public const string Optimized = "optimized";
    public const string Full = "full";

    public static string Normalize(string? value)
    {
        return string.Equals(value?.Trim(), Full, StringComparison.OrdinalIgnoreCase)
            ? Full
            : Optimized;
    }
}
