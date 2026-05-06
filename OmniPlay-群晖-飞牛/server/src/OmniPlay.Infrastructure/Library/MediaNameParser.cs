using System.Text.RegularExpressions;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.Library;

public static partial class MediaNameParser
{
    private static readonly string[] CleanupPatterns =
    [
        @"\b(1080p|2160p|4k|720p|480p|blu[- ]?ray|bluray|bdrip|web[- ]?dl|webrip|remux|x264|x265|h\.?264|h\.?265|hevc|aac|dts|hdr|dv)\b",
        @"\b[sS]\d{1,2}[eE][pP]?\d{1,3}\b",
        @"\b[sS]\d{1,2}\b",
        @"\b[eE][pP]?\d{1,3}\b",
        @"\bepisode[\s._-]*\d{1,3}\b",
        @"\b(?:part|pt)[\s._-]*\d{1,2}\b",
        @"\bseason\s*\d{1,2}\b",
        @"\bcomplete\b",
        @"\b\d{1,2}bit\b",
        @"\b(aac|ac3|eac3|ddp|dts|truehd|flac|mp3)\d*(\.\d+)?\b",
        @"\b(bonus|extras?|featurette|behind[- ]?the[- ]?scenes|trailer|sample)\b",
        @"\b(disc|disk|cd|dvd)\s*[-_ ]?\d{0,2}\b",
        @"\b(bdrom|bdmv)\b",
        @"\b(vol|volume)\s*[-_ ]?\d{1,2}\b",
        @"\b(special\s*features?|featurettes?)\b",
        @"\b(anniversary|edition|proper|repack|extended|remastered|unrated)\b",
        @"(剧场版|纪念版|花絮|特典|番外|幕后花絮)"
    ];

    public static string CleanedTitleSource(string rawPath)
    {
        var normalized = rawPath.Replace('\\', '/');
        var fileStem = Path.GetFileNameWithoutExtension(normalized);
        if (string.IsNullOrWhiteSpace(fileStem))
        {
            return rawPath;
        }

        var parentName = Path.GetFileName(Path.GetDirectoryName(normalized) ?? string.Empty);
        if (!ContainsHan(fileStem) && ContainsHan(parentName))
        {
            return $"{parentName} {fileStem}".Trim();
        }

        return fileStem;
    }

    public static SearchMetadata ExtractSearchMetadata(string rawPath)
    {
        var originalText = CleanedTitleSource(rawPath);
        var textToParse = TruncateBeforeReleaseMetadata(originalText);
        var extractedYear = ChooseReleaseYear(originalText) ?? ChooseReleaseYear(textToParse);

        if (!string.IsNullOrWhiteSpace(extractedYear))
        {
            textToParse = PreferTitleTextBeforeReleaseYear(textToParse, extractedYear);
        }

        textToParse = BracketLikeCharactersRegex().Replace(textToParse, " ");
        foreach (var pattern in CleanupPatterns)
        {
            textToParse = Regex.Replace(textToParse, pattern, " ", RegexOptions.IgnoreCase);
        }

        textToParse = Regex.Replace(textToParse, @"[._]+", " ");
        textToParse = Regex.Replace(textToParse, @"\s+", " ").Trim();

        var tokens = Tokenize(textToParse);
        return new SearchMetadata(
            ExtractChineseTitle(tokens),
            ExtractForeignTitle(tokens),
            string.IsNullOrWhiteSpace(textToParse) ? null : textToParse,
            extractedYear);
    }

    public static EpisodeInfo ParseEpisodeInfo(string fileName, int fallbackIndex)
    {
        var episode = fallbackIndex + 1;
        var season = 1;

        var seasonEpisodeMatch = Regex.Match(fileName, @"[sS](\d{1,2})[eE][pP]?(\d{1,3})");
        if (seasonEpisodeMatch.Success)
        {
            season = ParseOrDefault(seasonEpisodeMatch.Groups[1].Value, 1);
            episode = ParseOrDefault(seasonEpisodeMatch.Groups[2].Value, episode);
            return new EpisodeInfo(season, episode, BuildEpisodeDisplayName(season, episode), true);
        }

        var episodeOnlyMatch = Regex.Match(fileName, @"(?<![a-zA-Z])[eE][pP]?(\d{1,3})");
        if (episodeOnlyMatch.Success)
        {
            episode = ParseOrDefault(episodeOnlyMatch.Groups[1].Value, episode);
            return new EpisodeInfo(season, episode, BuildEpisodeDisplayName(season, episode), true);
        }

        var chineseEpisodeMatch = Regex.Match(fileName, @"第\s*(\d{1,3})\s*[集话]");
        if (chineseEpisodeMatch.Success)
        {
            episode = ParseOrDefault(chineseEpisodeMatch.Groups[1].Value, episode);
            return new EpisodeInfo(season, episode, BuildEpisodeDisplayName(season, episode), true);
        }

        return new EpisodeInfo(season, episode, fileName, false);
    }

    public static bool IsLikelyTvEpisodePath(string rawPath)
    {
        var lower = rawPath.Replace('\\', '/').ToLowerInvariant();
        return Regex.IsMatch(lower, @"[s]\d{1,2}[e][p]?\d{1,3}")
               || Regex.IsMatch(lower, @"(?<![a-z])ep?\d{1,3}\b")
               || Regex.IsMatch(lower, @"\bseason[\s._-]*\d{1,2}\b")
               || Regex.IsMatch(lower, @"第\s*\d{1,3}\s*[集话]");
    }

    public static string NormalizeRelativePath(string relativePath)
    {
        var normalized = relativePath
            .Replace('\\', '/')
            .Replace(Path.AltDirectorySeparatorChar, '/')
            .Replace(Path.DirectorySeparatorChar, '/');

        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized.TrimStart('/');
    }

    private static string BuildEpisodeDisplayName(int season, int episode)
    {
        return season == 0 ? $"特别篇 第 {episode} 集" : $"第 {season} 季 第 {episode} 集";
    }

    private static string? ChooseReleaseYear(string input)
    {
        var matches = Regex.Matches(input, @"(?<!\d)(19\d{2}|20\d{2})(?!\d)");
        return matches
            .Select(static match => match.Value)
            .Where(static year => int.TryParse(year, out var value) && value >= 1900 && value <= 2099)
            .LastOrDefault();
    }

    private static string PreferTitleTextBeforeReleaseYear(string input, string year)
    {
        var index = input.IndexOf(year, StringComparison.Ordinal);
        return index > 0 ? input[..index] : input;
    }

    private static string TruncateBeforeReleaseMetadata(string input)
    {
        var match = Regex.Match(input, @"\b(1080p|2160p|4k|720p|blu[- ]?ray|web[- ]?dl|webrip|remux)\b", RegexOptions.IgnoreCase);
        return match.Success && match.Index > 0 ? input[..match.Index] : input;
    }

    private static string? ExtractChineseTitle(IReadOnlyList<string> tokens)
    {
        var groups = new List<string>();
        var current = new List<string>();

        foreach (var token in tokens)
        {
            if (ContainsHan(token))
            {
                current.Add(token);
                continue;
            }

            if (current.Count > 0)
            {
                groups.Add(string.Concat(current));
                current.Clear();
            }
        }

        if (current.Count > 0)
        {
            groups.Add(string.Concat(current));
        }

        return groups.OrderByDescending(static x => x.Length).FirstOrDefault(static x => x.Length > 0);
    }

    private static string? ExtractForeignTitle(IReadOnlyList<string> tokens)
    {
        var filtered = tokens
            .Where(static token => !ContainsHan(token))
            .Where(static token => Regex.IsMatch(token, @"^(?=.*[\p{L}0-9])[\p{L}0-9'&:+-]+$"))
            .Where(static token => !Regex.IsMatch(token, @"^\d+$"))
            .Take(8)
            .ToArray();

        return filtered.Length == 0 ? null : string.Join(' ', filtered);
    }

    private static IReadOnlyList<string> Tokenize(string input)
    {
        return input
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => !string.IsNullOrWhiteSpace(token))
            .ToArray();
    }

    private static bool ContainsHan(string? input)
    {
        return !string.IsNullOrWhiteSpace(input) && Regex.IsMatch(input, @"\p{IsCJKUnifiedIdeographs}");
    }

    private static int ParseOrDefault(string input, int fallback)
    {
        return int.TryParse(input, out var value) ? value : fallback;
    }

    [GeneratedRegex(@"[\[\]\(\)\{\}【】（）《》「」『』〔〕〖〗]")]
    private static partial Regex BracketLikeCharactersRegex();
}

