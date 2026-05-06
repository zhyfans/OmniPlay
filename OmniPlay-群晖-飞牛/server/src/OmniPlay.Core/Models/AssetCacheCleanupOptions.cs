namespace OmniPlay.Core.Models;

public sealed record AssetCacheCleanupOptions(
    bool IncludeUntrackedFiles = true);
