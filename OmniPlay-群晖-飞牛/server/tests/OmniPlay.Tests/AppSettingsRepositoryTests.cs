using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.FileSystem;
using Xunit;

namespace OmniPlay.Tests;

public sealed class AppSettingsRepositoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omniplay-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UpdatePersistsAndNormalizesCacheSettings()
    {
        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();
        var repository = new AppSettingsRepository(database);

        var updated = await repository.UpdateAsync(new AppSettingsUpdateRequest(
            Cache: new CacheSettings(
                HlsRetentionHours: 999,
                ImageCleanupScope: "orphans-only",
                WebDavRetentionHours: 0,
                WebDavMaxGb: 0)));
        var snapshot = await repository.GetAsync();

        Assert.Equal(24 * 30, updated.Cache.HlsRetentionHours);
        Assert.Equal("orphans-only", updated.Cache.ImageCleanupScope);
        Assert.Equal(72, updated.Cache.WebDavRetentionHours);
        Assert.Equal(20, updated.Cache.WebDavMaxGb);
        Assert.Equal(24 * 30, snapshot.Cache.HlsRetentionHours);
        Assert.Equal("orphans-only", snapshot.Cache.ImageCleanupScope);
        Assert.Equal(72, snapshot.Cache.WebDavRetentionHours);
        Assert.Equal(20, snapshot.Cache.WebDavMaxGb);
    }

    [Fact]
    public async Task UpdatePersistsPlaybackSettings()
    {
        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "playback")));
        database.EnsureInitialized();
        var repository = new AppSettingsRepository(database);

        var updated = await repository.UpdateAsync(new AppSettingsUpdateRequest(
            Playback: new PlaybackSettings(
                DirectStream: false,
                HlsRemux: true,
                Transcode: false)));
        var snapshot = await repository.GetAsync();

        Assert.False(updated.Playback.DirectStream);
        Assert.True(updated.Playback.HlsRemux);
        Assert.False(updated.Playback.Transcode);
        Assert.False(snapshot.Playback.DirectStream);
        Assert.True(snapshot.Playback.HlsRemux);
        Assert.False(snapshot.Playback.Transcode);
    }

    [Fact]
    public async Task UpdateNormalizesEmptyPlaybackPolicyToDefault()
    {
        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "playback-default")));
        database.EnsureInitialized();
        var repository = new AppSettingsRepository(database);

        var updated = await repository.UpdateAsync(new AppSettingsUpdateRequest(
            Playback: new PlaybackSettings(
                DirectStream: false,
                HlsRemux: false,
                Transcode: false)));

        Assert.True(updated.Playback.DirectStream);
        Assert.True(updated.Playback.HlsRemux);
        Assert.True(updated.Playback.Transcode);
    }

    [Fact]
    public async Task UpdateCacheOnlyPreservesPlaybackSettings()
    {
        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "partial-update")));
        database.EnsureInitialized();
        var repository = new AppSettingsRepository(database);

        await repository.UpdateAsync(new AppSettingsUpdateRequest(
            Playback: new PlaybackSettings(
                DirectStream: false,
                HlsRemux: true,
                Transcode: false)));
        var updated = await repository.UpdateAsync(new AppSettingsUpdateRequest(
            Cache: new CacheSettings(
                HlsRetentionHours: 12,
                ImageCleanupScope: "orphans-and-untracked",
                WebDavRetentionHours: 48,
                WebDavMaxGb: 64)));

        Assert.False(updated.Playback.DirectStream);
        Assert.True(updated.Playback.HlsRemux);
        Assert.False(updated.Playback.Transcode);
        Assert.Equal(48, updated.Cache.WebDavRetentionHours);
        Assert.Equal(64, updated.Cache.WebDavMaxGb);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
