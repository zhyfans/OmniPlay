using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface IAssetCacheCleanupService
{
    Task<AssetCacheCleanupSummary> CleanupOrphansAsync(
        AssetCacheCleanupOptions? options = null,
        IProgress<BackgroundTaskProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
