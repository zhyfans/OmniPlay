namespace OmniPlay.Core.Models;

public sealed record WebDavCacheCleanupSummary(
    int RemovedFileCount,
    long RemovedBytes);
