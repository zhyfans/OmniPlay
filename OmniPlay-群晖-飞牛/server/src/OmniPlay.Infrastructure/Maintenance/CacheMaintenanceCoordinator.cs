using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.Maintenance;

public sealed class CacheMaintenanceCoordinator
{
    private readonly IAppSettingsRepository appSettingsRepository;
    private readonly IHlsSessionService hlsSessionService;
    private readonly IWebDavCacheCleanupService webDavCacheCleanupService;
    private readonly IAssetCacheCleanupService assetCacheCleanupService;
    private readonly IBackgroundTaskCenter backgroundTaskCenter;

    public CacheMaintenanceCoordinator(
        IAppSettingsRepository appSettingsRepository,
        IHlsSessionService hlsSessionService,
        IWebDavCacheCleanupService webDavCacheCleanupService,
        IAssetCacheCleanupService assetCacheCleanupService,
        IBackgroundTaskCenter backgroundTaskCenter)
    {
        this.appSettingsRepository = appSettingsRepository;
        this.hlsSessionService = hlsSessionService;
        this.webDavCacheCleanupService = webDavCacheCleanupService;
        this.assetCacheCleanupService = assetCacheCleanupService;
        this.backgroundTaskCenter = backgroundTaskCenter;
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (HasActiveBackgroundTask())
        {
            return;
        }

        var settings = await appSettingsRepository.GetAsync(cancellationToken);

        if (HasActiveBackgroundTask())
        {
            return;
        }

        var hlsRetentionHours = Math.Clamp(settings.Cache.HlsRetentionHours, 1, 24 * 30);
        hlsSessionService.CleanupCache(TimeSpan.FromHours(hlsRetentionHours));

        if (HasActiveBackgroundTask())
        {
            return;
        }

        var webDavRetentionHours = Math.Clamp(settings.Cache.WebDavRetentionHours, 1, 24 * 30);
        var maxBytes = Math.Max(1, settings.Cache.WebDavMaxGb) * 1024L * 1024L * 1024L;
        await webDavCacheCleanupService.CleanupAsync(
            TimeSpan.FromHours(webDavRetentionHours),
            cancellationToken: cancellationToken,
            maxBytes: maxBytes);

        if (HasActiveBackgroundTask())
        {
            return;
        }

        var includeUntrackedFiles = !string.Equals(
            settings.Cache.ImageCleanupScope,
            "orphans-only",
            StringComparison.OrdinalIgnoreCase);
        await assetCacheCleanupService.CleanupOrphansAsync(
            new AssetCacheCleanupOptions(includeUntrackedFiles),
            cancellationToken: cancellationToken);
    }

    private bool HasActiveBackgroundTask()
    {
        return backgroundTaskCenter.GetSnapshot().ActiveTask is not null;
    }
}
