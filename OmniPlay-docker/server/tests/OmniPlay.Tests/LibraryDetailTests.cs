using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.FileSystem;
using OmniPlay.Infrastructure.Library;
using Xunit;

namespace OmniPlay.Tests;

public sealed class LibraryDetailTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omniplay-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetItemDetailReturnsSeasonsEpisodesAndPlaybackState()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(mediaRoot, "Shows", "绝命毒师", "Season 01", "Breaking.Bad.S01E01.mkv"));
        Touch(Path.Combine(mediaRoot, "Shows", "绝命毒师", "Season 01", "Breaking.Bad.S01E02.mkv"));

        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        await new LibraryScanner(database).ScanAllAsync();

        var repository = new LibraryRepository(database);
        var item = (await repository.GetItemsAsync()).Single();
        Assert.False(item.IsWatched);
        var detail = await repository.GetItemDetailAsync(item.Id);

        Assert.NotNull(detail);
        Assert.False(detail.IsWatched);
        Assert.Equal("tv", detail.ItemKind);
        Assert.Equal("绝命毒师", detail.Title);
        Assert.Equal(2, detail.VideoFiles.Count);
        Assert.Single(detail.Seasons);
        Assert.Equal(1, detail.Seasons[0].SeasonNumber);
        Assert.Equal(2, detail.Seasons[0].Episodes.Count);

        var firstFile = detail.Seasons[0].Episodes[0].VideoFile!;
        Assert.True(await repository.UpdateVideoFileProbeAsync(new VideoFileProbeUpdate(
            firstFile.Id,
            DurationSeconds: 1234,
            Container: "matroska,webm",
            VideoCodec: "h264",
            AudioCodec: "aac",
            SubtitleSummary: "subrip",
            ProbeJson: """
                {
                  "streams": [
                    {
                      "index": 1,
                      "codec_type": "audio",
                      "codec_name": "aac",
                      "channels": 2,
                      "channel_layout": "stereo",
                      "tags": { "language": "jpn", "title": "Japanese" },
                      "disposition": { "default": 1, "forced": 0 }
                    },
                    {
                      "index": 2,
                      "codec_type": "subtitle",
                      "codec_name": "subrip",
                      "tags": { "language": "chi", "title": "中文" },
                      "disposition": { "default": 0, "forced": 1 }
                    }
                  ]
                }
                """)));
        var probedFile = await repository.GetPlayableVideoFileAsync(firstFile.Id);
        Assert.NotNull(probedFile);
        Assert.Equal("matroska,webm", probedFile.Container);
        Assert.Equal("h264", probedFile.VideoCodec);
        Assert.Equal("aac", probedFile.AudioCodec);
        Assert.Equal("subrip", probedFile.SubtitleSummary);

        Assert.True(await repository.UpdatePlaybackProgressAsync(new PlaybackProgressUpdateRequest(
            firstFile.Id,
            PositionSeconds: 95,
            DurationSeconds: 100)));

        var updatedDetail = await repository.GetItemDetailAsync(item.Id);
        Assert.NotNull(updatedDetail);
        var updatedFile = updatedDetail.VideoFiles.First(file => file.Id == firstFile.Id);
        Assert.True(updatedFile.IsWatched);
        Assert.Equal(1234, updatedFile.DurationSeconds);
        Assert.Equal("matroska,webm", updatedFile.Container);
        Assert.Equal("h264", updatedFile.VideoCodec);
        Assert.Equal("aac", updatedFile.AudioCodec);
        Assert.Equal("subrip", updatedFile.SubtitleSummary);
        Assert.Single(updatedFile.AudioTracks);
        Assert.Equal(1, updatedFile.AudioTracks[0].Index);
        Assert.Equal("jpn", updatedFile.AudioTracks[0].Language);
        Assert.Equal("Japanese", updatedFile.AudioTracks[0].Title);
        Assert.Equal(2, updatedFile.AudioTracks[0].Channels);
        Assert.Single(updatedFile.SubtitleStreams);
        Assert.Equal(2, updatedFile.SubtitleStreams[0].Index);
        Assert.True(updatedFile.SubtitleStreams[0].IsForced);
        Assert.Equal(95, updatedDetail.MaxProgressSeconds);
        Assert.Equal(1234, updatedDetail.MaxDurationSeconds);

        Assert.True(await repository.UpdatePlaybackProgressAsync(new PlaybackProgressUpdateRequest(
            firstFile.Id,
            PositionSeconds: 0,
            DurationSeconds: 100)));
        var zeroUpdateDetail = await repository.GetItemDetailAsync(item.Id);
        Assert.NotNull(zeroUpdateDetail);
        Assert.Equal(95, zeroUpdateDetail.MaxProgressSeconds);

        Assert.True(await repository.SetWatchedAsync(new WatchedStatusUpdateRequest(firstFile.Id, IsWatched: false)));
        var unwatchedDetail = await repository.GetItemDetailAsync(item.Id);
        Assert.NotNull(unwatchedDetail);
        Assert.False(unwatchedDetail.VideoFiles.First(file => file.Id == firstFile.Id).IsWatched);

        Assert.True(await repository.SetLibraryItemWatchedAsync(new LibraryItemWatchedStatusUpdateRequest(item.Id, IsWatched: true)));
        var watchedDetail = await repository.GetItemDetailAsync(item.Id);
        Assert.NotNull(watchedDetail);
        Assert.True(watchedDetail.IsWatched);
        Assert.All(watchedDetail.VideoFiles, file => Assert.True(file.IsWatched));
        Assert.True((await repository.GetItemsAsync()).Single().IsWatched);

        Assert.True(await repository.SetLibraryItemWatchedAsync(new LibraryItemWatchedStatusUpdateRequest(item.Id, IsWatched: false)));
        var wholeItemUnwatchedDetail = await repository.GetItemDetailAsync(item.Id);
        Assert.NotNull(wholeItemUnwatchedDetail);
        Assert.False(wholeItemUnwatchedDetail.IsWatched);
        Assert.All(wholeItemUnwatchedDetail.VideoFiles, file => Assert.False(file.IsWatched));
    }

    [Fact]
    public async Task ApplyMetadataMatchUpdatesAndLocksLibraryItem()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(mediaRoot, "Movies", "Unknown.Movie.2020.mkv"));

        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        await new LibraryScanner(database).ScanAllAsync();

        var repository = new LibraryRepository(database);
        var item = (await repository.GetItemsAsync()).Single();

        Assert.True(await repository.ApplyMetadataMatchAsync(new LibraryItemMetadataApplyRequest(
            item.Id,
            TmdbId: 123,
            MediaType: "movie",
            Title: "手动匹配影片",
            Overview: "手动选择的简介。",
            ReleaseDate: "2020-05-01",
            PosterPath: null,
            VoteAverage: 7.8,
            PosterLocalPath: null,
            LockMetadata: true)));

        var lockedDetail = await repository.GetItemDetailAsync(item.Id);
        Assert.NotNull(lockedDetail);
        Assert.Equal("手动匹配影片", lockedDetail.Title);
        Assert.Equal("手动选择的简介。", lockedDetail.Overview);
        Assert.Equal("2020-05-01", lockedDetail.ReleaseDate);
        Assert.Equal(7.8, lockedDetail.VoteAverage);
        Assert.True(lockedDetail.IsLocked);
        Assert.Equal(123, lockedDetail.TmdbId);

        var customPosterPath = Path.Combine(root, "custom-poster.jpg");
        File.WriteAllBytes(customPosterPath, [1, 2, 3, 4]);
        Assert.True(await repository.UpdateCustomMetadataAsync(new LibraryItemCustomMetadataUpdateRequest(
            item.Id,
            Title: "手动编辑影片",
            ReleaseDate: "",
            Overview: "",
            VoteAverage: null,
            DoubanRating: null,
            PosterLocalPath: customPosterPath,
            PosterRemotePath: "custom-upload",
            LockMetadata: true)));

        var customDetail = await repository.GetItemDetailAsync(item.Id);
        Assert.NotNull(customDetail);
        Assert.Equal("手动编辑影片", customDetail.Title);
        Assert.Null(customDetail.ReleaseDate);
        Assert.Null(customDetail.Overview);
        Assert.Null(customDetail.VoteAverage);
        Assert.NotNull(customDetail.PosterAssetId);
        Assert.True(customDetail.IsLocked);

        Assert.True(await repository.SetLibraryItemLockedAsync(new LibraryItemLockUpdateRequest(item.Id, IsLocked: false)));
        var unlockedDetail = await repository.GetItemDetailAsync(item.Id);
        Assert.NotNull(unlockedDetail);
        Assert.False(unlockedDetail.IsLocked);
    }

    [Fact]
    public async Task GetItemDetailHandlesMultipleFilesForSameEpisode()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(mediaRoot, "Shows", "绝命毒师", "Season 01", "Breaking.Bad.S01E01.1080p.mkv"));
        Touch(Path.Combine(mediaRoot, "Shows", "绝命毒师", "Season 01", "Breaking.Bad.S01E01.2160p.mkv"));

        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        await new LibraryScanner(database).ScanAllAsync();

        var repository = new LibraryRepository(database);
        var item = Assert.Single(await repository.GetItemsAsync());
        var detail = await repository.GetItemDetailAsync(item.Id);

        Assert.NotNull(detail);
        Assert.Equal(2, detail.VideoFiles.Count);
        Assert.Single(detail.Seasons);
        Assert.Single(detail.Seasons[0].Episodes);
        Assert.NotNull(detail.Seasons[0].Episodes[0].VideoFile);
    }

    [Fact]
    public async Task GetItemDetailSortsSpecialSeasonAfterNumberedSeasons()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(mediaRoot, "Shows", "示例剧", "示例剧.S00E01.mkv"));
        Touch(Path.Combine(mediaRoot, "Shows", "示例剧", "示例剧.S01E01.mkv"));
        Touch(Path.Combine(mediaRoot, "Shows", "示例剧", "示例剧.S02E01.mkv"));

        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        await new LibraryScanner(database).ScanAllAsync();

        var repository = new LibraryRepository(database);
        var item = Assert.Single(await repository.GetItemsAsync());
        var detail = await repository.GetItemDetailAsync(item.Id);

        Assert.NotNull(detail);
        Assert.Equal([1, 2, 0], detail.Seasons.Select(static season => season.SeasonNumber));
        Assert.Equal([1, 2, 0], detail.VideoFiles.Select(static file => file.SeasonNumber ?? -1));
    }

    [Fact]
    public async Task UpdateCustomMetadataCanSetEpisodeSubtitle()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(mediaRoot, "Shows", "示例综艺", "示例综艺.S01E01.mkv"));

        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        await new LibraryScanner(database).ScanAllAsync();

        var repository = new LibraryRepository(database);
        var item = Assert.Single(await repository.GetItemsAsync());
        var detail = await repository.GetItemDetailAsync(item.Id);
        Assert.NotNull(detail);
        var episode = Assert.Single(detail.Seasons[0].Episodes);

        Assert.True(await repository.UpdateCustomMetadataAsync(new LibraryItemCustomMetadataUpdateRequest(
            item.Id,
            Title: "示例综艺",
            ReleaseDate: detail.ReleaseDate,
            Overview: detail.Overview,
            VoteAverage: detail.VoteAverage,
            DoubanRating: null,
            EpisodeId: episode.Id,
            EpisodeSubtitle: "加更")));

        var updated = await repository.GetItemDetailAsync(item.Id);
        Assert.NotNull(updated);
        Assert.Equal("示例综艺", updated.Title);
        Assert.Equal("示例综艺 第 1 季 第 1 集·加更", Assert.Single(updated.Seasons[0].Episodes).Title);
    }

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0, 1, 2, 3]);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
