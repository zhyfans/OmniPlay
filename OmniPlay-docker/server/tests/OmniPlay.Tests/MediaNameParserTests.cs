using OmniPlay.Infrastructure.Library;
using Xunit;

namespace OmniPlay.Tests;

public sealed class MediaNameParserTests
{
    [Fact]
    public void ExtractSearchMetadataPrefersChineseParentTitle()
    {
        var metadata = MediaNameParser.ExtractSearchMetadata(
            "/media/霸王别姬 (1993)/Farewell.My.Concubine.1993.1080p.BluRay.mkv");

        Assert.Equal("霸王别姬", metadata.ChineseTitle);
        Assert.Equal("1993", metadata.Year);
    }

    [Fact]
    public void ParseEpisodeInfoReadsSeasonEpisodePattern()
    {
        var episode = MediaNameParser.ParseEpisodeInfo("Breaking.Bad.S01E02.mkv", 0);

        Assert.True(episode.IsTvShow);
        Assert.Equal(1, episode.Season);
        Assert.Equal(2, episode.Episode);
    }

    [Theory]
    [InlineData(
        "/电影/12.12.The.Day.2023.HKG.Blu-ray.1080p.AVC.TrueHD.5.1-Breeze@Sunny/BDMV/STREAM/00003.m2ts",
        "12 12 The Day",
        "2023")]
    [InlineData(
        "Judgement at Nuremberg 1961 GER Blu-ray 1080p AVC DTS-HD MA 5.1-pt520@HDSky/BDMV/STREAM/00004.m2ts",
        "Judgement at Nuremberg",
        "1961")]
    [InlineData(
        "Gone.with.the.Wind.1939/Disc - Gone with the Wind/BDMV/STREAM/00027.m2ts",
        "Gone with the Wind",
        "1939")]
    public void ExtractSearchMetadataUsesBluRayFolderNameInsteadOfStreamNumber(
        string path,
        string expectedForeignTitle,
        string expectedYear)
    {
        var metadata = MediaNameParser.ExtractSearchMetadata(path);

        Assert.Equal(expectedForeignTitle, metadata.ForeignTitle);
        Assert.Equal(expectedYear, metadata.Year);
        Assert.DoesNotContain("000", metadata.FullCleanTitle ?? string.Empty);
    }

    [Fact]
    public void CombinedSearchMetadataKeepsChineseAndForeignTitles()
    {
        var metadata = MediaNameParser.CombinedSearchMetadata(
            "足球教练.Ted.Lasso.2020.S01.2160p.ATVP.WEB-DL.H265/Ted.Lasso.2020.S01E01.2160p.ATVP.WEB-DL.H265.mkv",
            "Ted.Lasso.2020.S01E01.2160p.ATVP.WEB-DL.H265.mkv");

        Assert.Equal("足球教练", metadata.ChineseTitle);
        Assert.Equal("Ted Lasso", metadata.ForeignTitle);
        Assert.Equal("2020", metadata.Year);
    }

    [Theory]
    [InlineData(
        "操控游戏.The.Manipulated.S01E01.2025.2160p.DSNP.WEB-DL.DDP5.1.H265.HDR.DV-Pure@HDSWEB.mkv",
        "操控游戏",
        "The Manipulated",
        "2025")]
    [InlineData(
        "罗小黑战记2.The.Legend.of.Hei.2.2025.2160p.HQ.WEB-DL.H265.DV.DTS-ADWeb.mkv",
        "罗小黑战记2",
        "The Legend of Hei 2",
        "2025")]
    public void CombinedSearchMetadataKeepsReportedChineseTitleBeforeReleaseTags(
        string fileName,
        string expectedChineseTitle,
        string expectedForeignTitle,
        string expectedYear)
    {
        var metadata = MediaNameParser.CombinedSearchMetadata(fileName, fileName);

        Assert.Equal(expectedChineseTitle, metadata.ChineseTitle);
        Assert.Equal(expectedForeignTitle, metadata.ForeignTitle);
        Assert.Equal(expectedYear, metadata.Year);
    }

    [Theory]
    [InlineData(
        "Stranger.Things.S04.2160p.NF.WEB-DL.DDP.5.1.Atmos.HDR10.H.265-CHDWEB/Stranger.Things.S04E01.Chapter.One.The.Hellfire.Club.2160p.NF.WEB-DL.DDP.5.1.Atmos.HDR10.H.265-CHDWEB.mkv",
        "怪奇物语")]
    [InlineData(
        "The.Glory.S01.2160p.NF.WEB-DL.DDP5.1.Atmos.DV.HDR.HEVC-HHWEB/The.Glory.S01E01.Episode.1.2160p.NF.WEB-DL.DDP5.1.Atmos.DV.HDR.HEVC-HHWEB.mkv",
        "黑暗荣耀")]
    public void NormalizeTvSeriesTitleUsesKnownChineseAliases(string path, string expectedTitle)
    {
        var metadata = MediaNameParser.ExtractSearchMetadata(path);

        Assert.Equal(expectedTitle, MediaNameParser.NormalizeTvSeriesTitle(metadata.ForeignTitle!));
    }

    [Theory]
    [InlineData(
        "Bicycle Thieves 1948 CC Blu-ray 1080p AVC LPCM 1.0-blucook#303@CHDBits/Bicycle.Thieves.1948.CC.Blu-ray.1080p.AVC.LPCM.1.0@blucook#303.iso",
        "Bicycle Thieves",
        "1948")]
    [InlineData(
        "Parasite.2019.2160p.KOR.UHD.Blu-ray.HEVC.Atmos.TrueHD.7.1-DiY@HDHome/BDMV/STREAM/00003.m2ts",
        "Parasite",
        "2019")]
    [InlineData(
        "Signal.S01.2016.1080p.BluRay.Remux.AVC.DTS-HD.MA.2.0.2Audio-ADE/Signal.S01E01.2016.1080p.BluRay.Remux.AVC.DTS-HD.MA.2.0.2Audio-ADE.mkv",
        "Signal",
        "2016")]
    [InlineData(
        "Monogatari.Series.S01.2009.1080p.BluRay.Remux.AVC.FLAC.2.0-ADE/Monogatari.Series.S00E02.2009.1080p.BluRay.Remux.AVC.FLAC.2.0-ADE.mkv",
        "Monogatari Series",
        "2009")]
    public void ExtractSearchMetadataHandlesReportedReleaseNames(
        string path,
        string expectedForeignTitle,
        string expectedYear)
    {
        var metadata = MediaNameParser.ExtractSearchMetadata(path);

        Assert.Equal(expectedForeignTitle, metadata.ForeignTitle);
        Assert.Equal(expectedYear, metadata.Year);
    }

    [Fact]
    public void ExtractSearchMetadataDoesNotUseLeadingNumericTitleAsReleaseYear()
    {
        var metadata = MediaNameParser.ExtractSearchMetadata(
            "1917 逆战救兵 2019 UHD Blu-ray 2160p HEVC TrueHD Atmos 7.1-Pete@HDSky/1917 4K Ultra HD.iso");

        Assert.Equal("2019", metadata.Year);
        Assert.Equal("逆战救兵", metadata.ChineseTitle);
    }

    [Fact]
    public void ExtractSearchMetadataRemovesTvStationLogoTokens()
    {
        var metadata = MediaNameParser.ExtractSearchMetadata(
            "CCTV4K.Aerial.China.S01.Complete.2020.UHDTV.HEVC.HLG.DD5.1-CMCTV/CCTV4K.Aerial.China.S01E01.2020.UHDTV.HEVC.HLG.DD5.1-CMCTV.ts");

        Assert.Equal("Aerial China", metadata.ForeignTitle);
        Assert.Equal("2020", metadata.Year);
    }

    [Fact]
    public void ResolvePreferredSeasonKeepsFateZeroSpecialSeason()
    {
        const string path = "命运之夜前传特典：拜托了！爱因兹贝伦咨询室.Fate ∕ Zero.Einzbern.Counseling.Room.S00.2012.1080p.Blu-ray.Remux.LPCM 2.0-LuckAni/命运之夜前传特典：拜托了！爱因兹贝伦咨询室.Fate ∕ Zero.Einzbern.Counseling.Room.S00E01.2012.1080p.Blu-ray.Remux.LPCM 2.0-LuckAni.mkv";
        var metadata = MediaNameParser.ExtractSearchMetadata(path);

        Assert.Equal("命运之夜前传", MediaNameParser.NormalizeTvSeriesTitle(metadata.ChineseTitle!));
        Assert.Equal(0, MediaNameParser.ResolvePreferredSeason(path, Path.GetFileName(path)));
    }

    [Fact]
    public void ResolvePreferredSeasonKeepsMinamiKeFourthSeason()
    {
        const string path = "南家三姐妹 我回来了.Minami-ke Tadaima.S04.2013.1080p.Blu-Ray.x265.FLAC 2.0-LuckAni/南家三姐妹 我回来了.Minami-ke Tadaima.S04E01.2013.1080p.Blu-Ray.x265.FLAC 2.0-LuckAni.mkv";
        var metadata = MediaNameParser.ExtractSearchMetadata(path);

        Assert.Equal("南家三姐妹", MediaNameParser.NormalizeTvSeriesTitle(metadata.ChineseTitle!));
        Assert.Equal(4, MediaNameParser.ResolvePreferredSeason(path, Path.GetFileName(path)));
    }

    [Theory]
    [InlineData(
        "Hayate no Gotoku Cuties S04 2013 1080p BluRay Remux AVC DTS-HD MA 2.0-LuckAni/Hayate no Gotoku Cuties S04E01 2013 1080p BluRay Remux AVC DTS-HD MA 2.0-LuckAni.mkv",
        "旋风管家")]
    [InlineData(
        "Hayate the Combat Butler Season 2 S02 2009 1080p BluRay Remux AVC DTS 2.0-LuckAni/Hayate the Combat Butler Season 2 S02E01 2009 1080p BluRay Remux AVC DTS 2.0-LuckAni.mkv",
        "旋风管家")]
    public void NormalizeTvSeriesTitleUsesKnownHayateAliases(string path, string expectedTitle)
    {
        var metadata = MediaNameParser.ExtractSearchMetadata(path);

        Assert.Equal(expectedTitle, MediaNameParser.NormalizeTvSeriesTitle(metadata.ForeignTitle!));
    }

    [Fact]
    public void BuildEpisodeSubtitlePrefersDifferentChineseToken()
    {
        var podcastTokens = MediaNameParser.ExtractEpisodeSubtitleTokens(
            "乘风破浪的姐姐.播客.Sisters.Who.Make.Waves.S07E01.Talk.2026.2160p.WEB-DL.H265.AAC-ADWeb.mkv");
        var programTokens = MediaNameParser.ExtractEpisodeSubtitleTokens(
            "乘风破浪的姐姐.企划.Sisters.Who.Make.Waves.S07E01.Program.2026.2160p.WEB-DL.H265.AAC-ADWeb.mkv");
        var common = podcastTokens.Select(static token => token.Key)
            .Intersect(programTokens.Select(static token => token.Key), StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal("播客", MediaNameParser.BuildEpisodeSubtitle(podcastTokens.Where(token => !common.Contains(token.Key)).ToArray()));
        Assert.Equal("企划", MediaNameParser.BuildEpisodeSubtitle(programTokens.Where(token => !common.Contains(token.Key)).ToArray()));
    }
}
