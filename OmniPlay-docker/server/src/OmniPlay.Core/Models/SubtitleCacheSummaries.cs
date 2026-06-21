namespace OmniPlay.Core.Models;

public sealed record SubtitleCachePrewarmSummary(
    int CandidateTrackCount,
    int CachedTrackCount,
    int SkippedTrackCount,
    long CachedBytes);

public sealed record SubtitleCacheCleanupSummary(
    int RemovedFileCount,
    long RemovedBytes);

public sealed record SubtitleCacheStatus(
    int PgsTotal,
    int PgsCached,
    long CachedBytes,
    int SubtitleTotal = 0,
    int SubtitleCached = 0);
