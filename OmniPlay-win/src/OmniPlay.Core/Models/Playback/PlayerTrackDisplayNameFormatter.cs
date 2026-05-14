using System.Text.RegularExpressions;

namespace OmniPlay.Core.Models.Playback;

public static class PlayerTrackDisplayNameFormatter
{
    public static string Format(
        string fallbackPrefix,
        long trackId,
        string? title,
        string? language,
        string? codec = null,
        string? audioChannels = null,
        bool isDefault = false,
        bool isForced = false,
        bool isExternal = false)
    {
        var rawTitle = title?.Trim() ?? string.Empty;
        var rawLanguage = language?.Trim() ?? string.Empty;
        var languageLabel = TranslateLanguageCode(rawLanguage);
        var displayParts = new List<string>();

        if (!string.IsNullOrWhiteSpace(languageLabel))
        {
            displayParts.Add(languageLabel);
        }

        var codecSearchText = string.Join(
            ' ',
            new[] { codec, rawTitle }
                .Select(static value => value?.Trim())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase));
        var formattedCodec = FormatCodec(codecSearchText);
        var formattedChannels = FormatAudioChannels(audioChannels);
        if (string.IsNullOrWhiteSpace(formattedChannels))
        {
            formattedChannels = ExtractAudioChannels(rawTitle);
        }

        if (!string.IsNullOrWhiteSpace(rawTitle) &&
            !IsDuplicateTrackTitle(rawTitle, rawLanguage, languageLabel) &&
            !IsAudioMetadataOnlyTitle(rawTitle, formattedCodec, formattedChannels))
        {
            displayParts.Add(rawTitle);
        }

        var baseName = displayParts.Count == 0
            ? $"{fallbackPrefix} {trackId}"
            : string.Join(" - ", displayParts);

        var metadataParts = new List<string>();

        if (!string.IsNullOrWhiteSpace(formattedCodec) && !string.IsNullOrWhiteSpace(formattedChannels))
        {
            metadataParts.Add($"{formattedCodec} {formattedChannels}");
        }
        else if (!string.IsNullOrWhiteSpace(formattedCodec))
        {
            metadataParts.Add(formattedCodec);
        }
        else if (!string.IsNullOrWhiteSpace(formattedChannels))
        {
            metadataParts.Add(formattedChannels);
        }

        if (isDefault)
        {
            metadataParts.Add("默认");
        }

        if (isForced)
        {
            metadataParts.Add("强制");
        }

        if (isExternal)
        {
            metadataParts.Add("外挂");
        }

        return metadataParts.Count == 0
            ? baseName
            : $"{baseName} ({string.Join(" / ", metadataParts)})";
    }

    private static bool IsAudioMetadataOnlyTitle(
        string title,
        string formattedCodec,
        string formattedChannels)
    {
        var normalized = NormalizeAudioMetadataTitle(title);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        if (string.Equals(normalized, "surround51", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var codecNormalized = NormalizeAudioMetadataTitle(formattedCodec);
        var channelNormalized = NormalizeAudioMetadataTitle(formattedChannels);
        var hasCodec = !string.IsNullOrWhiteSpace(codecNormalized) &&
                       normalized.Contains(codecNormalized, StringComparison.OrdinalIgnoreCase);
        var hasChannel = !string.IsNullOrWhiteSpace(channelNormalized) &&
                         normalized.Contains(channelNormalized, StringComparison.OrdinalIgnoreCase);
        if (hasCodec && (hasChannel || LooksLikeAudioCodecOnlyTitle(normalized)))
        {
            return true;
        }

        if (!RegexContainsAudioChannel(title))
        {
            return false;
        }

        var withoutChannel = Regex.Replace(
            normalized,
            @"(?:mono|stereo|channels?|ch|10|20|21|40|41|50|51|61|70|71|1|2|3|4|5|6|7|8)$",
            string.Empty,
            RegexOptions.IgnoreCase);
        return LooksLikeAudioCodecOnlyTitle(withoutChannel);
    }

    private static bool LooksLikeAudioCodecOnlyTitle(string normalizedTitle)
    {
        return normalizedTitle is "dts" or "dtshd" or "dtshdma" or "dtshdmasteraudio"
            or "truehd" or "truehdatmos" or "atmos" or "ac3" or "eac3" or "ddp"
            or "dolbydigital" or "dolbydigitalplus" or "aac" or "flac" or "lpcm" or "pcm";
    }

    private static string NormalizeAudioMetadataTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value
            .Where(static character => char.IsLetterOrDigit(character))
            .Select(static character => char.ToLowerInvariant(character))
            .ToArray());
    }

    private static bool IsDuplicateTrackTitle(string title, string language, string languageLabel)
    {
        if (string.Equals(title, language, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(title, languageLabel, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizedTitle = NormalizeTrackLabelForComparison(title);
        var normalizedLanguage = NormalizeTrackLabelForComparison(language);
        var normalizedLanguageLabel = NormalizeTrackLabelForComparison(languageLabel);

        return (!string.IsNullOrWhiteSpace(normalizedLanguage) &&
                string.Equals(normalizedTitle, normalizedLanguage, StringComparison.OrdinalIgnoreCase)) ||
            GetLanguageAliases(language, languageLabel)
                .Select(NormalizeTrackLabelForComparison)
                .Where(static alias => !string.IsNullOrWhiteSpace(alias))
                .Any(alias => string.Equals(normalizedTitle, alias, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(normalizedLanguageLabel) &&
             (string.Equals(normalizedTitle, normalizedLanguageLabel, StringComparison.OrdinalIgnoreCase) ||
              normalizedTitle.Contains(normalizedLanguageLabel, StringComparison.OrdinalIgnoreCase) ||
              normalizedLanguageLabel.Contains(normalizedTitle, StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<string> GetLanguageAliases(string language, string languageLabel)
    {
        if (!string.IsNullOrWhiteSpace(languageLabel))
        {
            yield return languageLabel;
        }

        foreach (var token in language.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            switch (token.Trim().ToLowerInvariant())
            {
                case "eng":
                case "en":
                    yield return "English";
                    yield return "\u82F1\u8BED";
                    break;
                case "jpn":
                case "ja":
                    yield return "Japanese";
                    yield return "\u65E5\u8BED";
                    break;
                case "kor":
                case "ko":
                    yield return "Korean";
                    yield return "\u97E9\u8BED";
                    break;
                case "chi":
                case "zho":
                case "zh":
                case "chs":
                case "cht":
                case "cmn":
                case "yue":
                    yield return "Chinese";
                    yield return "\u4E2D\u6587";
                    yield return "\u7B80\u4F53";
                    yield return "\u7E41\u4F53";
                    break;
                case "fre":
                case "fra":
                case "fr":
                    yield return "French";
                    yield return "\u6CD5\u8BED";
                    break;
                case "spa":
                case "es":
                    yield return "Spanish";
                    yield return "\u897F\u8BED";
                    break;
                case "ger":
                case "deu":
                case "de":
                    yield return "German";
                    yield return "\u5FB7\u8BED";
                    break;
                case "rus":
                case "ru":
                    yield return "Russian";
                    yield return "\u4FC4\u8BED";
                    break;
                case "ita":
                case "it":
                    yield return "Italian";
                    yield return "\u610F\u8BED";
                    break;
                case "por":
                case "pt":
                    yield return "Portuguese";
                    yield return "\u8461\u8BED";
                    break;
                case "tha":
                case "th":
                    yield return "Thai";
                    yield return "\u6CF0\u8BED";
                    break;
                case "vie":
                case "vi":
                    yield return "Vietnamese";
                    yield return "\u8D8A\u5357\u8BED";
                    break;
            }
        }
    }

    private static string NormalizeTrackLabelForComparison(string value)
    {
        return new string(value
            .Where(static character => char.IsLetterOrDigit(character))
            .ToArray());
    }

    public static string TranslateLanguageCode(string? language)
    {
        var trimmed = language?.Trim() ?? string.Empty;
        var normalized = trimmed.ToLowerInvariant().Replace('_', '-');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var primaryCode = normalized.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? normalized;
        if (primaryCode is "chi" or "zho" or "zh" or "cmn" or "yue")
        {
            return "🇨🇳 中文";
        }

        return primaryCode switch
        {
            "chs" or "cht" => "🇨🇳 中文",
            "eng" or "en" => "🇺🇸 英语",
            "jpn" or "ja" => "🇯🇵 日语",
            "kor" or "ko" => "🇰🇷 韩语",
            "fre" or "fra" or "fr" => "🇫🇷 法语",
            "spa" or "es" => "🇪🇸 西语",
            "ger" or "deu" or "de" => "🇩🇪 德语",
            "rus" or "ru" => "🇷🇺 俄语",
            "ita" or "it" => "🇮🇹 意语",
            "por" or "pt" => "🇵🇹 葡语",
            "tha" or "th" => "🇹🇭 泰语",
            "vie" or "vi" => "🇻🇳 越南语",
            _ => trimmed.ToUpperInvariant()
        };
    }

    public static string FormatCodec(string? codec)
    {
        var normalized = codec?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        normalized = normalized
            .Replace('_', '-')
            .Replace('.', '-');

        if (normalized.Contains("truehd", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("true-hd", StringComparison.OrdinalIgnoreCase))
        {
            return normalized.Contains("atmos", StringComparison.OrdinalIgnoreCase)
                ? "TrueHD Atmos"
                : "TrueHD";
        }

        if (normalized.Contains("dts-hd", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("dtshd", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("dts hd", StringComparison.OrdinalIgnoreCase))
        {
            return normalized.Contains("hra", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains("high resolution", StringComparison.OrdinalIgnoreCase)
                ? "DTS-HD HRA"
                : "DTS-HD MA";
        }

        if (normalized.Contains("dts", StringComparison.OrdinalIgnoreCase))
        {
            return "DTS";
        }

        if (normalized.Contains("eac3", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("e-ac-3", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("ddp", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("dolby digital plus", StringComparison.OrdinalIgnoreCase))
        {
            return "Dolby Digital Plus (E-AC3)";
        }

        if (normalized.Contains("ac3", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("ac-3", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("dolby digital", StringComparison.OrdinalIgnoreCase))
        {
            return "Dolby Digital (AC3)";
        }

        if (normalized.Contains("aac", StringComparison.OrdinalIgnoreCase))
        {
            return "AAC";
        }

        if (normalized.Contains("flac", StringComparison.OrdinalIgnoreCase))
        {
            return "FLAC";
        }

        if (normalized.Contains("lpcm", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("pcm", StringComparison.OrdinalIgnoreCase))
        {
            return "LPCM";
        }

        if (normalized.Contains("pgs", StringComparison.OrdinalIgnoreCase))
        {
            return "PGS 图形字幕";
        }

        if (normalized.Contains("srt", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("subrip", StringComparison.OrdinalIgnoreCase))
        {
            return "SRT";
        }

        if (normalized.Contains("ass", StringComparison.OrdinalIgnoreCase))
        {
            return "ASS";
        }

        return normalized.ToUpperInvariant();
    }

    public static string FormatAudioChannels(string? channels)
    {
        var normalized = channels?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var lower = normalized.ToLowerInvariant();
        lower = Regex.Replace(lower, @"\s*channels?\s*$", string.Empty, RegexOptions.IgnoreCase);
        lower = Regex.Replace(lower, @"\s*ch\s*$", string.Empty, RegexOptions.IgnoreCase);
        lower = Regex.Replace(lower, @"\s*\([^)]*\)\s*$", string.Empty, RegexOptions.IgnoreCase);
        lower = lower.Trim();

        return lower switch
        {
            "1" => "1.0",
            "2" => "2.0",
            "3" => "2.1",
            "4" => "4.0",
            "5" => "5.0",
            "6" => "5.1",
            "7" => "6.1",
            "8" => "7.1",
            "stereo" => "2.0",
            "mono" => "1.0",
            _ => Regex.IsMatch(lower, @"^\d(?:\.\d)?$", RegexOptions.IgnoreCase)
                ? lower
                : normalized
        };
    }

    private static string ExtractAudioChannels(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var match = Regex.Match(
            text,
            @"(?<!\d)(?<channels>[1-8](?:\.[01])?)(?:\s*(?:ch|channels?))?(?!\d)",
            RegexOptions.IgnoreCase);
        return match.Success ? FormatAudioChannels(match.Groups["channels"].Value) : string.Empty;
    }

    private static bool RegexContainsAudioChannel(string value)
    {
        return Regex.IsMatch(
            value,
            @"(?<!\d)[1-8](?:\.[01])?(?:\s*(?:ch|channels?))?(?!\d)|\b(?:mono|stereo)\b",
            RegexOptions.IgnoreCase);
    }
}
