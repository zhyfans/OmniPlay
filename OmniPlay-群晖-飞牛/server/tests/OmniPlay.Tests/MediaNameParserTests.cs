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
}

