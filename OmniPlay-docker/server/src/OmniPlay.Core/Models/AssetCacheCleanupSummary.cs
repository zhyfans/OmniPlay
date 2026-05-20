namespace OmniPlay.Core.Models;

public sealed record AssetCacheCleanupSummary(
    int ScannedAssetCount,
    int RemovedAssetRecordCount,
    int RemovedFileCount,
    long RemovedBytes);
