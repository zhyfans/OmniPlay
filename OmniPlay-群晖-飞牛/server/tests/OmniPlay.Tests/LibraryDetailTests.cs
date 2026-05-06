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
        var detail = await repository.GetItemDetailAsync(item.Id);

        Assert.NotNull(detail);
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

        Assert.True(await repository.SetWatchedAsync(new WatchedStatusUpdateRequest(firstFile.Id, IsWatched: false)));
        var unwatchedDetail = await repository.GetItemDetailAsync(item.Id);
        Assert.NotNull(unwatchedDetail);
        Assert.False(unwatchedDetail.VideoFiles.First(file => file.Id == firstFile.Id).IsWatched);
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

        Assert.True(await repository.SetLibraryItemLockedAsync(new LibraryItemLockUpdateRequest(item.Id, IsLocked: false)));
        var unlockedDetail = await repository.GetItemDetailAsync(item.Id);
        Assert.NotNull(unlockedDetail);
        Assert.False(unlockedDetail.IsLocked);
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
