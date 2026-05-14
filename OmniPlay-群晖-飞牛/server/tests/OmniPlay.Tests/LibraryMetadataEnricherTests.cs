using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.FileSystem;
using OmniPlay.Infrastructure.Library;
using Xunit;

namespace OmniPlay.Tests;

public sealed class LibraryMetadataEnricherTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omniplay-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EnrichMissingUpdatesMetadataAndPosterAsset()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(mediaRoot, "Movies", "Farewell.My.Concubine.1993.1080p.mkv"));

        var paths = new StoragePaths(Path.Combine(root, "app"));
        var database = new SqliteDatabase(paths);
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        await new LibraryScanner(database).ScanAllAsync();

        var fakeTmdb = new FakeTmdbMetadataClient(paths);
        var enricher = new LibraryMetadataEnricher(
            database,
            new AppSettingsRepository(database),
            fakeTmdb);

        var summary = await enricher.EnrichMissingAsync();
        var items = await new LibraryRepository(database).GetItemsAsync();

        Assert.Equal(1, summary.ScannedItems);
        Assert.Equal(1, summary.MatchedItems);
        Assert.Equal(1, summary.UpdatedItems);
        Assert.Equal(1, summary.DownloadedPosters);
        Assert.Single(items);
        Assert.Equal("霸王别姬", items[0].Title);
        Assert.Equal("一段横跨时代的故事。", items[0].Overview);
        Assert.Equal(8.2, items[0].VoteAverage);
        Assert.False(string.IsNullOrWhiteSpace(items[0].PosterAssetId));

        var detail = await new LibraryRepository(database).GetItemDetailAsync(items[0].Id);
        Assert.NotNull(detail);
        Assert.Equal(1, detail.TmdbId);
    }

    [Fact]
    public async Task EnrichItemUsesSavedTmdbIdForPreciseRefresh()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(mediaRoot, "Movies", "Farewell.My.Concubine.1993.1080p.mkv"));

        var paths = new StoragePaths(Path.Combine(root, "app"));
        var database = new SqliteDatabase(paths);
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        await new LibraryScanner(database).ScanAllAsync();

        var fakeTmdb = new FakeTmdbMetadataClient(paths);
        var enricher = new LibraryMetadataEnricher(
            database,
            new AppSettingsRepository(database),
            fakeTmdb);

        await enricher.EnrichMissingAsync();
        var item = (await new LibraryRepository(database).GetItemsAsync()).Single();

        fakeTmdb.ResetCounters();
        fakeTmdb.SearchMatch = new TmdbMetadataMatch(
            999,
            "movie",
            "错误搜索结果",
            "标题搜索不应被使用。",
            "1999-01-01",
            "/wrong.jpg",
            3.1,
            1);
        fakeTmdb.DetailMatch = new TmdbMetadataMatch(
            1,
            "movie",
            "霸王别姬 4K 修复版",
            "按已保存 TMDB ID 精确刷新的简介。",
            "1993-01-01",
            "/poster-4k.jpg",
            8.9,
            20);

        var summary = await enricher.EnrichItemAsync(item.Id);
        var detail = await new LibraryRepository(database).GetItemDetailAsync(item.Id);

        Assert.Equal(1, summary.MatchedItems);
        Assert.Equal(1, summary.UpdatedItems);
        Assert.Equal(0, fakeTmdb.SearchRequestCount);
        Assert.Equal(1, fakeTmdb.DetailRequestCount);
        Assert.Equal(1, fakeTmdb.LastDetailTmdbId);
        Assert.NotNull(detail);
        Assert.Equal("霸王别姬 4K 修复版", detail.Title);
        Assert.Equal("按已保存 TMDB ID 精确刷新的简介。", detail.Overview);
        Assert.Equal(8.9, detail.VoteAverage);
        Assert.Equal(1, detail.TmdbId);
    }

    [Fact]
    public async Task EnrichItemRefreshesExistingTvEpisodeMetadata()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(mediaRoot, "Shows", "Breaking Bad", "Season 01", "Breaking.Bad.S01E01.mkv"));
        Touch(Path.Combine(mediaRoot, "Shows", "Breaking Bad", "Season 01", "Breaking.Bad.S01E02.mkv"));

        var paths = new StoragePaths(Path.Combine(root, "app"));
        var database = new SqliteDatabase(paths);
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        await new LibraryScanner(database).ScanAllAsync();

        var fakeTmdb = new FakeTmdbMetadataClient(paths)
        {
            SearchMatch = new TmdbMetadataMatch(
                1396,
                "tv",
                "绝命毒师",
                "一名化学老师的故事。",
                "2008-01-20",
                "/show.jpg",
                8.9,
                100)
        };
        fakeTmdb.SeasonDetails[1] = new TmdbSeasonDetail(
            1,
            "第 1 季",
            "第一季简介。",
            "2008-01-20",
            "/season.jpg",
            [
                new TmdbEpisodeDetail(
                    1,
                    "试播集",
                    "沃尔特面临人生转折。",
                    "2008-01-20",
                    "/still-1.jpg"),
                new TmdbEpisodeDetail(
                    2,
                    "猫在袋里",
                    "杰西和沃尔特处理后果。",
                    "2008-01-27",
                    "/still-2.jpg"),
                new TmdbEpisodeDetail(
                    3,
                    "本地没有的集",
                    "不应创建新分集。",
                    "2008-02-10",
                    "/still-3.jpg")
            ]);

        var enricher = new LibraryMetadataEnricher(
            database,
            new AppSettingsRepository(database),
            fakeTmdb);

        var item = (await new LibraryRepository(database).GetItemsAsync()).Single();
        var summary = await enricher.EnrichItemAsync(item.Id);
        var detail = await new LibraryRepository(database).GetItemDetailAsync(item.Id);

        Assert.Equal(1, summary.MatchedItems);
        Assert.Equal(4, summary.DownloadedPosters);
        Assert.Equal(1, fakeTmdb.SeasonRequestCount);
        Assert.Equal(new[] { 1 }, fakeTmdb.LastSeasonNumbers);
        Assert.NotNull(detail);
        Assert.Equal(1396, detail.TmdbId);
        Assert.Single(detail.Seasons);
        Assert.Equal("第 1 季", detail.Seasons[0].Title);
        Assert.False(string.IsNullOrWhiteSpace(detail.Seasons[0].PosterAssetId));
        Assert.Equal(2, detail.Seasons[0].Episodes.Count);
        Assert.Equal("试播集", detail.Seasons[0].Episodes[0].Title);
        Assert.Equal("沃尔特面临人生转折。", detail.Seasons[0].Episodes[0].Overview);
        Assert.Equal("2008-01-20", detail.Seasons[0].Episodes[0].AirDate);
        Assert.False(string.IsNullOrWhiteSpace(detail.Seasons[0].Episodes[0].StillAssetId));
        Assert.Equal("猫在袋里", detail.Seasons[0].Episodes[1].Title);
        Assert.Equal("杰西和沃尔特处理后果。", detail.Seasons[0].Episodes[1].Overview);
        Assert.Equal("2008-01-27", detail.Seasons[0].Episodes[1].AirDate);
        Assert.False(string.IsNullOrWhiteSpace(detail.Seasons[0].Episodes[1].StillAssetId));
    }

    [Fact]
    public async Task EnrichMissingUsesSourceForeignTitleAsSecondaryQuery()
    {
        var mediaRoot = Path.Combine(root, "media");
        Touch(Path.Combine(
            mediaRoot,
            "Shows",
            "足球教练.Ted.Lasso.2020.S01.2160p.ATVP.WEB-DL.H265",
            "Ted.Lasso.2020.S01E01.2160p.ATVP.WEB-DL.H265.mkv"));

        var paths = new StoragePaths(Path.Combine(root, "app"));
        var database = new SqliteDatabase(paths);
        database.EnsureInitialized();

        await new MediaSourceRepository(database).AddLocalAsync("测试媒体", mediaRoot);
        await new LibraryScanner(database).ScanAllAsync();

        var fakeTmdb = new FakeTmdbMetadataClient(paths)
        {
            SearchMatch = new TmdbMetadataMatch(
                97546,
                "tv",
                "足球教练",
                "美式足球教练执教英超球队。",
                "2020-08-14",
                "/ted-lasso.jpg",
                8.4,
                80)
        };
        var enricher = new LibraryMetadataEnricher(
            database,
            new AppSettingsRepository(database),
            fakeTmdb);

        await enricher.EnrichMissingAsync();

        Assert.Equal("足球教练", fakeTmdb.LastSearchTitle);
        Assert.Equal("Ted Lasso", fakeTmdb.LastSearchSecondaryQuery);
        Assert.Equal("2020", fakeTmdb.LastSearchYear);
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

    private sealed class FakeTmdbMetadataClient : OmniPlay.Core.Interfaces.ITmdbMetadataClient
    {
        private readonly StoragePaths paths;

        public FakeTmdbMetadataClient(StoragePaths paths)
        {
            this.paths = paths;
        }

        public int SearchRequestCount { get; private set; }

        public int DetailRequestCount { get; private set; }

        public int SeasonRequestCount { get; private set; }

        public int? LastDetailTmdbId { get; private set; }

        public string? LastSearchTitle { get; private set; }

        public string? LastSearchYear { get; private set; }

        public string? LastSearchSecondaryQuery { get; private set; }

        public List<int> LastSeasonNumbers { get; } = [];

        public TmdbMetadataMatch SearchMatch { get; set; } = new(
            1,
            "movie",
            "霸王别姬",
            "一段横跨时代的故事。",
            "1993-01-01",
            "/poster.jpg",
            8.2,
            10);

        public TmdbMetadataMatch? DetailMatch { get; set; }

        public Dictionary<int, TmdbSeasonDetail> SeasonDetails { get; } = [];

        public void ResetCounters()
        {
            SearchRequestCount = 0;
            DetailRequestCount = 0;
            SeasonRequestCount = 0;
            LastDetailTmdbId = null;
            LastSearchTitle = null;
            LastSearchYear = null;
            LastSearchSecondaryQuery = null;
            LastSeasonNumbers.Clear();
        }

        public Task<TmdbConnectionTestResult> TestConnectionAsync(
            TmdbSettings settings,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TmdbConnectionTestResult(true, "fake", 200, "ok"));
        }

        public Task<TmdbMetadataMatch?> SearchAsync(
            string mediaType,
            string title,
            string? year,
            TmdbSettings settings,
            string? secondaryQuery = null,
            CancellationToken cancellationToken = default)
        {
            SearchRequestCount++;
            LastSearchTitle = title;
            LastSearchYear = year;
            LastSearchSecondaryQuery = secondaryQuery;
            return Task.FromResult<TmdbMetadataMatch?>(SearchMatch with { MediaType = mediaType });
        }

        public Task<IReadOnlyList<TmdbMetadataMatch>> SearchCandidatesAsync(
            string mediaType,
            string title,
            string? year,
            TmdbSettings settings,
            string? secondaryQuery = null,
            int limit = 8,
            CancellationToken cancellationToken = default)
        {
            LastSearchTitle = title;
            LastSearchYear = year;
            LastSearchSecondaryQuery = secondaryQuery;
            IReadOnlyList<TmdbMetadataMatch> matches =
            [
                SearchMatch with { MediaType = mediaType }
            ];
            return Task.FromResult(matches);
        }

        public Task<TmdbMetadataMatch?> GetDetailsAsync(
            string mediaType,
            int tmdbId,
            TmdbSettings settings,
            CancellationToken cancellationToken = default)
        {
            DetailRequestCount++;
            LastDetailTmdbId = tmdbId;
            return Task.FromResult<TmdbMetadataMatch?>(
                (DetailMatch ?? SearchMatch) with { Id = tmdbId, MediaType = mediaType });
        }

        public Task<TmdbSeasonDetail?> GetSeasonAsync(
            int tvTmdbId,
            int seasonNumber,
            TmdbSettings settings,
            CancellationToken cancellationToken = default)
        {
            SeasonRequestCount++;
            LastSeasonNumbers.Add(seasonNumber);
            SeasonDetails.TryGetValue(seasonNumber, out var season);
            return Task.FromResult<TmdbSeasonDetail?>(season);
        }

        public Task<string?> DownloadPosterAsync(
            string posterPath,
            string mediaType,
            int tmdbId,
            CancellationToken cancellationToken = default)
        {
            paths.EnsureCreated();
            var poster = Path.Combine(paths.PostersDirectory, "poster.jpg");
            File.WriteAllBytes(poster, [1, 2, 3]);
            return Task.FromResult<string?>(poster);
        }

        public Task<string?> DownloadStillAsync(
            string stillPath,
            int tvTmdbId,
            int seasonNumber,
            int episodeNumber,
            CancellationToken cancellationToken = default)
        {
            paths.EnsureCreated();
            var still = Path.Combine(paths.ThumbnailsDirectory, $"s{seasonNumber:00}e{episodeNumber:00}.jpg");
            File.WriteAllBytes(still, [4, 5, 6]);
            return Task.FromResult<string?>(still);
        }
    }
}
