using OmniPlay.Core.Models.Playback;

namespace OmniPlay.Tests;

public sealed class PlayerTrackDisplayNameFormatterTests
{
    [Fact]
    public void Format_BuildsMacStyleAudioTrackName()
    {
        var displayName = PlayerTrackDisplayNameFormatter.Format(
            "音轨",
            2,
            "Main",
            "eng",
            "dts-hd ma",
            "5.1",
            isDefault: true);

        Assert.Equal("🇺🇸 英语 - Main (DTS-HD MA 5.1 / 默认)", displayName);
    }

    [Fact]
    public void Format_NormalizesNumericAudioChannels()
    {
        var displayName = PlayerTrackDisplayNameFormatter.Format(
            "音轨",
            3,
            title: null,
            language: "eng",
            codec: "dts",
            audioChannels: "6");

        Assert.Equal("🇺🇸 英语 (DTS 5.1)", displayName);
    }

    [Fact]
    public void Format_UsesAudioMetadataTitleAsCodecHintWithoutDuplicatingIt()
    {
        var displayName = PlayerTrackDisplayNameFormatter.Format(
            "音轨",
            4,
            "DTS 6",
            language: null,
            codec: null,
            audioChannels: null);

        Assert.Equal("音轨 4 (DTS 5.1)", displayName);
    }

    [Fact]
    public void Format_PrefersDetailedCodecFromTrackTitle()
    {
        var displayName = PlayerTrackDisplayNameFormatter.Format(
            "音轨",
            5,
            "DTS-HD Master Audio 7.1",
            "jpn",
            "dts",
            "8");

        Assert.Equal("🇯🇵 日语 (DTS-HD MA 7.1)", displayName);
    }

    [Fact]
    public void Format_BuildsMacStyleSubtitleTrackName()
    {
        var displayName = PlayerTrackDisplayNameFormatter.Format(
            "字幕",
            7,
            "简体字幕",
            "chi",
            "hdmv_pgs_subtitle",
            isExternal: true);

        Assert.Equal("🇨🇳 中文 - 简体字幕 (PGS 图形字幕 / 外挂)", displayName);
    }

    [Theory]
    [InlineData("jpn", "🇯🇵 日语")]
    [InlineData("zh-Hans", "🇨🇳 中文")]
    [InlineData("ZH_HANT", "🇨🇳 中文")]
    [InlineData("en-US", "🇺🇸 英语")]
    [InlineData("ja-JP", "🇯🇵 日语")]
    [InlineData("kor", "🇰🇷 韩语")]
    [InlineData("fra", "🇫🇷 法语")]
    [InlineData("deu", "🇩🇪 德语")]
    [InlineData("und", "UND")]
    public void TranslateLanguageCode_UsesMacStyleLabels(string language, string expected)
    {
        Assert.Equal(expected, PlayerTrackDisplayNameFormatter.TranslateLanguageCode(language));
    }
}
