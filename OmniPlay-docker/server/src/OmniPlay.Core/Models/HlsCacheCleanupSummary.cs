namespace OmniPlay.Core.Models;

public sealed record HlsCacheCleanupSummary(
    int RemovedSessionCount,
    long RemovedBytes);
