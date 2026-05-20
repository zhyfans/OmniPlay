using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.Maintenance;
using Xunit;

namespace OmniPlay.Tests;

public sealed class CacheMaintenanceCoordinatorTests
{
    [Fact]
    public async Task RunOnceAsyncRunsAllMaintenanceStepsWhenIdle()
    {
        var settings = new AppSettingsSnapshot(
            "OmniPlay",
            "phase-2",
            new TmdbSettings(),
            new CacheSettings(
                HlsRetentionHours: 12,
                ImageCleanupScope: "orphans-only",
                WebDavRetentionHours: 48,
                WebDavMaxGb: 7),
            new PlaybackSettings(),
            new ProxySettings(),
            new AutomationSettings());
        var hlsService = new RecordingHlsSessionService();
        var webDavService = new RecordingWebDavCacheCleanupService();
        var assetService = new RecordingAssetCacheCleanupService();
        var coordinator = new CacheMaintenanceCoordinator(
            new FixedSettingsRepository(settings),
            hlsService,
            webDavService,
            assetService,
            new IdleBackgroundTaskCenter());

        await coordinator.RunOnceAsync();

        Assert.Equal(TimeSpan.FromHours(12), hlsService.LastRetention);
        Assert.Equal(TimeSpan.FromHours(48), webDavService.LastRetention);
        Assert.Equal(7L * 1024 * 1024 * 1024, webDavService.LastMaxBytes);
        Assert.Equal(1, hlsService.CallCount);
        Assert.Equal(1, webDavService.CallCount);
        Assert.Equal(1, assetService.CallCount);
        Assert.False(assetService.LastOptions!.IncludeUntrackedFiles);
    }

    [Fact]
    public async Task RunOnceAsyncSkipsWhenABackgroundTaskIsActive()
    {
        var hlsService = new RecordingHlsSessionService();
        var webDavService = new RecordingWebDavCacheCleanupService();
        var assetService = new RecordingAssetCacheCleanupService();
        var coordinator = new CacheMaintenanceCoordinator(
            new FixedSettingsRepository(new AppSettingsSnapshot(
                "OmniPlay",
                "phase-2",
                new TmdbSettings(),
                new CacheSettings(),
                new PlaybackSettings(),
                new ProxySettings(),
                new AutomationSettings())),
            hlsService,
            webDavService,
            assetService,
            new BusyBackgroundTaskCenter());

        await coordinator.RunOnceAsync();

        Assert.Equal(0, hlsService.CallCount);
        Assert.Equal(0, webDavService.CallCount);
        Assert.Equal(0, assetService.CallCount);
    }

    private sealed class FixedSettingsRepository : IAppSettingsRepository
    {
        private readonly AppSettingsSnapshot snapshot;

        public FixedSettingsRepository(AppSettingsSnapshot snapshot)
        {
            this.snapshot = snapshot;
        }

        public Task<AppSettingsSnapshot> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(snapshot);
        }

        public Task<AppSettingsSnapshot> UpdateAsync(
            AppSettingsUpdateRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(snapshot);
        }
    }

    private sealed class IdleBackgroundTaskCenter : IBackgroundTaskCenter
    {
        public BackgroundTaskSnapshot GetSnapshot()
        {
            return new BackgroundTaskSnapshot(Array.Empty<BackgroundTaskStatus>(), null);
        }

        public bool TryStartExclusive(
            string kind,
            string title,
            Func<string, IProgress<BackgroundTaskProgress>, CancellationToken, Task<string?>> executeAsync,
            Action<DateTimeOffset>? onAccepted,
            out BackgroundTaskStatus status)
        {
            throw new NotImplementedException();
        }

        public bool TryCancel(string taskId, out BackgroundTaskStatus status)
        {
            throw new NotImplementedException();
        }

        public bool TryCancelKind(string kind, out BackgroundTaskStatus status)
        {
            throw new NotImplementedException();
        }
    }

    private sealed class BusyBackgroundTaskCenter : IBackgroundTaskCenter
    {
        public BackgroundTaskSnapshot GetSnapshot()
        {
            var now = DateTimeOffset.UtcNow;
            var active = new BackgroundTaskStatus(
                "task-1",
                "scan",
                "扫描媒体库",
                "running",
                true,
                false,
                true,
                now,
                now,
                null,
                "running",
                "处理中",
                null,
                null,
                null,
                null);
            return new BackgroundTaskSnapshot([active], active);
        }

        public bool TryStartExclusive(
            string kind,
            string title,
            Func<string, IProgress<BackgroundTaskProgress>, CancellationToken, Task<string?>> executeAsync,
            Action<DateTimeOffset>? onAccepted,
            out BackgroundTaskStatus status)
        {
            throw new NotImplementedException();
        }

        public bool TryCancel(string taskId, out BackgroundTaskStatus status)
        {
            throw new NotImplementedException();
        }

        public bool TryCancelKind(string kind, out BackgroundTaskStatus status)
        {
            throw new NotImplementedException();
        }
    }

    private sealed class RecordingHlsSessionService : IHlsSessionService
    {
        public int CallCount { get; private set; }
        public TimeSpan? LastRetention { get; private set; }

        public Task<HlsPlaybackSession> EnsureSessionAsync(
            PlayableVideoFile file,
            HlsPlaybackProfile profile,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public HlsPlaybackAsset? GetAsset(string sessionId, string assetName)
        {
            return null;
        }

        public Task<FfmpegTranscodeCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public string PreviewCommand(PlayableVideoFile file, HlsPlaybackProfile profile)
        {
            throw new NotImplementedException();
        }

        public bool StopSession(string sessionId)
        {
            throw new NotImplementedException();
        }

        public HlsCacheCleanupSummary CleanupCache(TimeSpan maxAge)
        {
            CallCount++;
            LastRetention = maxAge;
            return new HlsCacheCleanupSummary(0, 0);
        }
    }

    private sealed class RecordingWebDavCacheCleanupService : IWebDavCacheCleanupService
    {
        public int CallCount { get; private set; }
        public TimeSpan? LastRetention { get; private set; }
        public long? LastMaxBytes { get; private set; }

        public Task<WebDavCacheCleanupSummary> CleanupAsync(
            TimeSpan maxAge,
            IProgress<BackgroundTaskProgress>? progress = null,
            CancellationToken cancellationToken = default,
            long? maxBytes = null)
        {
            CallCount++;
            LastRetention = maxAge;
            LastMaxBytes = maxBytes;
            return Task.FromResult(new WebDavCacheCleanupSummary(0, 0));
        }
    }

    private sealed class RecordingAssetCacheCleanupService : IAssetCacheCleanupService
    {
        public int CallCount { get; private set; }
        public AssetCacheCleanupOptions? LastOptions { get; private set; }

        public Task<AssetCacheCleanupSummary> CleanupOrphansAsync(
            AssetCacheCleanupOptions? options = null,
            IProgress<BackgroundTaskProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastOptions = options;
            return Task.FromResult(new AssetCacheCleanupSummary(0, 0, 0, 0));
        }
    }
}
