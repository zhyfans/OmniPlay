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
}
