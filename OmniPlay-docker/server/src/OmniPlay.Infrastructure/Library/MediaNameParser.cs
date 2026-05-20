using System.Text.RegularExpressions;
using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.Tmdb;

namespace OmniPlay.Infrastructure.Library;

public static partial class MediaNameParser
{
    public readonly record struct BluRayStreamCandidate(
        string FileName,
        long FileSize,
        double Duration = 0);

    public sealed record CombinedSearchMetadataResult(
        string? ChineseTitle,
        string? ParentChineseTitle,
        string? ForeignTitle,
        string? FullCleanTitle,
        string? Year);

    public sealed record EpisodeSubtitleToken(string Text, string Key, bool ContainsHan);

    public static (int Number, string Name) BluRayStreamPlaybackSortKey(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).Trim();
        var number = int.TryParse(stem, out var parsed)
            ? parsed
            : int.MaxValue;
        return (number, fileName.ToLowerInvariant());
    }

    public static IReadOnlyList<int> SelectedBluRayStreamIndices(
        IReadOnlyList<BluRayStreamCandidate> candidates,
        bool includeExtras)
    {
        var ordered = Enumerable.Range(0, candidates.Count)
            .OrderBy(index => BluRayStreamPlaybackSortKey(candidates[index].FileName).Number)
            .ThenBy(index => BluRayStreamPlaybackSortKey(candidates[index].FileName).Name, StringComparer.Ordinal)
            .ToList();
        if (includeExtras)
        {
            return ordered;
        }

        if (ordered.Count == 0)
        {
            return [];
        }

        var bySize = SelectedBluRayMainFeatureIndices(
            ordered,
            candidates,
            candidate => Math.Max(0, candidate.FileSize));
        if (bySize is not null)
        {
            return bySize;
        }

        var byDuration = SelectedBluRayMainFeatureIndices(
            ordered,
            candidates,
            candidate => candidate.Duration >= 300 ? candidate.Duration : 0);
        if (byDuration is not null)
        {
            return byDuration;
        }

        return [ordered[0]];
    }

    private static IReadOnlyList<int>? SelectedBluRayMainFeatureIndices(
        IReadOnlyList<int> ordered,
        IReadOnlyList<BluRayStreamCandidate> candidates,
        Func<BluRayStreamCandidate, double> metric)
    {
        var known = ordered
            .Where(index => metric(candidates[index]) > 0)
            .ToList();
        if (known.Count == 0)
        {
            return null;
        }

        var byMetric = known
            .OrderByDescending(index => metric(candidates[index]))
            .ThenBy(index => BluRayStreamPlaybackSortKey(candidates[index].FileName).Number)
            .ThenBy(index => BluRayStreamPlaybackSortKey(candidates[index].FileName).Name, StringComparer.Ordinal)
            .ToList();
        var largest = byMetric[0];
        var largestMetric = metric(candidates[largest]);
        if (largestMetric <= 0)
        {
            return null;
        }

        if (byMetric.Count == 1)
        {
            return [largest];
        }

        var secondMetric = metric(candidates[byMetric[1]]);
        if (secondMetric <= 0 || largestMetric >= secondMetric * 1.55)
        {
            return [largest];
        }

        var clusterThreshold = largestMetric * 0.72;
        var cluster = ordered
            .Where(index => metric(candidates[index]) >= clusterThreshold)
            .ToList();
        return cluster.Count == 0 ? [largest] : cluster;
    }

    private static readonly string[] CleanupPatterns =
    [
        @"\b(1080p|2160p|4k|uhd|uhdtv|hdtv|720p|480p|blu[- ]?ray|bluray|bdrip|web[- ]?dl|webrip|remux|x264|x265|h\.?264|h\.?265|hevc|avc|vc[- ]?1|aac|dts[- ]?hd|dts|lpcm|truehd|hdr|hlg|dv|atmos)\b",
        @"\b[sS]\d{1,2}[eE][pP]?\d{1,3}\b",
        @"\b[sS]\d{1,2}\b",
        @"\b[eE][pP]?\d{1,3}\b",
        @"第\s*\d{1,3}\s*[集话]",
        @"\bepisode[\s._-]*\d{1,3}\b",
        @"\b(?:part|pt)[\s._-]*\d{1,2}\b",
        @"\bseason\s*\d{1,2}\b",
        @"\bcomplete\b",
        @"\b(cctv\d*k?|cmctv|cntv|nhk|wowow|bs11|mbs|tokyo\s*mx)\b",
        @"\b\d{1,2}bit\b",
        @"\b(aac|ac3|eac3|ddp|dts|dts[- ]?hd|truehd|lpcm|flac|mp3)\d*(\.\d+)?\b",
        @"\b(ma|hi10p|10bit|8bit)\b",
        @"\b(usa|ger|gbr|uk|jpn|jap|kor|chn|hkg|tw|fr|fra|ita|esp|rus|can|aus)\b",
        @"\b(bonus|extras?|featurette|behind[- ]?the[- ]?scenes|trailer|sample)\b",
        @"\b(disc|disk|cd|dvd)\b",
        @"\b(disc|disk|cd|dvd)\s*[-_ ]?\d{1,2}\b",
        @"\b(bdrom|bdmv)\b",
        @"\b(vol|volume)\s*[-_ ]?\d{1,2}([\-–]\d{1,2})?\b",
        @"\b(special\s*features?|featurettes?)\b",
        @"\b\d{1,3}(st|nd|rd|th)\s+anniversary(\s+edition)?\b",
        @"\b(anniversary|edition|proper|repack|extended|remastered|unrated)\b",
        @"\d{1,3}\s*周年\s*纪念版",
        @"(映画|剧场版|劇場版|電影版|电影版|完全版|总集篇|總集篇|特別篇|特别篇|纪念版|花絮|特典|番外|幕后花絮)"
    ];

    public static string CleanedTitleSource(string rawPath)
    {
        var normalized = rawPath.Replace('\\', '/');
        if (normalized.Contains("BDMV", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".m2ts", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".m2t", StringComparison.OrdinalIgnoreCase))
        {
            var components = normalized
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var bdmvIndex = FindPathComponent(components, "BDMV");
            if (bdmvIndex > 0)
            {
                var candidate = components[bdmvIndex - 1];
                if (IsGenericDiscOrVolumeFolder(candidate) && bdmvIndex > 1)
                {
                    candidate = components[bdmvIndex - 2];
                    if (bdmvIndex > 2)
                    {
                        candidate = MergeSplitTitleSegments(components[bdmvIndex - 3], candidate);
                    }
                }

                return candidate;
            }

            var streamIndex = FindPathComponent(components, "STREAM");
            if (streamIndex > 1)
            {
                var candidate = components[streamIndex - 2];
                if (IsGenericDiscOrVolumeFolder(candidate) && streamIndex > 2)
                {
                    candidate = components[streamIndex - 3];
                    if (streamIndex > 3)
                    {
                        candidate = MergeSplitTitleSegments(components[streamIndex - 4], candidate);
                    }
                }

                return candidate;
            }
        }

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
        var extractedYear = ChooseReleaseYear(rawPath) ?? ChooseReleaseYear(originalText) ?? ChooseReleaseYear(textToParse);

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

        var originalTokens = Tokenize(BracketLikeCharactersRegex().Replace(originalText, " "));
        var tokens = Tokenize(textToParse);
        return new SearchMetadata(
            ExtractChineseTitle(tokens) ?? ExtractChineseTitle(originalTokens),
            ExtractForeignTitle(tokens),
            string.IsNullOrWhiteSpace(textToParse) ? null : textToParse,
            extractedYear);
    }

    public static string? ExtractParentFolderChineseTitle(string rawPath)
    {
        var parent = Path.GetDirectoryName(rawPath)?.Trim();
        if (string.IsNullOrWhiteSpace(parent))
        {
            return null;
        }

        var parentName = Path.GetFileName(parent)?.Trim();
        if (string.IsNullOrWhiteSpace(parentName) || !ContainsHan(parentName))
        {
            return null;
        }

        return ExtractBracketedChineseTitle(parentName) ?? ExtractSearchMetadata(parentName).ChineseTitle;
    }

    public static CombinedSearchMetadataResult CombinedSearchMetadata(string relativePath, string fileName)
    {
        var useFileNameFirst = IsLikelyMediaServerEndpointPath(relativePath);
        var primaryRawPath = useFileNameFirst ? fileName : relativePath;
        var secondaryRawPath = useFileNameFirst ? relativePath : fileName;
        var primary = ExtractSearchMetadata(primaryRawPath);
        var secondary = ExtractSearchMetadata(secondaryRawPath);
        var parentChineseTitle = useFileNameFirst ? null : ExtractParentFolderChineseTitle(relativePath);

        return new CombinedSearchMetadataResult(
            NonEmpty(primary.ChineseTitle) ?? NonEmpty(secondary.ChineseTitle),
            NonEmpty(parentChineseTitle),
            NonEmpty(primary.ForeignTitle) ?? NonEmpty(secondary.ForeignTitle),
            NonEmpty(primary.FullCleanTitle) ?? NonEmpty(secondary.FullCleanTitle),
            NonEmpty(primary.Year) ?? NonEmpty(secondary.Year));
    }

    public static string BestDisplayTitle(string relativePath, string fileName)
    {
        var metadata = CombinedSearchMetadata(relativePath, fileName);
        return metadata.ChineseTitle
               ?? metadata.ParentChineseTitle
               ?? metadata.ForeignTitle
               ?? metadata.FullCleanTitle
               ?? Path.GetFileNameWithoutExtension(fileName);
    }

    public static string? ExtractedDisplayTitle(string relativePath, string fileName)
    {
        var metadata = CombinedSearchMetadata(relativePath, fileName);
        string?[] candidates =
        [
            metadata.ChineseTitle,
            metadata.ParentChineseTitle,
            metadata.ForeignTitle,
            metadata.FullCleanTitle
        ];

        foreach (var candidate in candidates)
        {
            var title = UsableDisplayTitle(candidate);
            if (title is not null && !LooksLikeRawReleaseName(title))
            {
                return title;
            }
        }

        return null;
    }

    public static bool IsUsableLibraryDisplayTitle(string? title)
    {
        var normalized = UsableDisplayTitle(title);
        return normalized is not null && !LooksLikeRawReleaseName(normalized);
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

    public static IReadOnlyList<EpisodeSubtitleToken> ExtractEpisodeSubtitleTokens(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return [];
        }

        var normalized = BracketLikeCharactersRegex().Replace(stem, " ");
        normalized = Regex.Replace(normalized, @"[._\-]+", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        var tokens = Tokenize(normalized);
        List<EpisodeSubtitleToken> subtitleTokens = [];
        var seenEpisodeMarker = false;
        foreach (var rawToken in tokens)
        {
            var token = rawToken.Trim().Trim('\'', '"', ',', ';');
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (IsEpisodeMarkerToken(token))
            {
                seenEpisodeMarker = true;
                continue;
            }

            if (seenEpisodeMarker && (IsYearToken(token) || IsReleaseMetadataToken(token)))
            {
                break;
            }

            if (IsYearToken(token)
                || IsReleaseMetadataToken(token)
                || IsEpisodeSubtitleNoiseToken(token)
                || Regex.IsMatch(token, @"^\d+(\.\d+)?$"))
            {
                continue;
            }

            var key = NormalizeSubtitleTokenKey(token);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            subtitleTokens.Add(new EpisodeSubtitleToken(token, key, ContainsHan(token)));
        }

        return subtitleTokens;
    }

    public static string? BuildEpisodeSubtitle(IReadOnlyList<EpisodeSubtitleToken> tokens)
    {
        var chineseTokens = tokens
            .Where(static token => token.ContainsHan)
            .Select(static token => token.Text)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (chineseTokens.Length > 0)
        {
            return string.Concat(chineseTokens).Trim();
        }

        var foreignTokens = tokens
            .Where(static token => !token.ContainsHan)
            .Select(static token => token.Text)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
        return foreignTokens.Length == 0 ? null : string.Join(' ', foreignTokens).Trim();
    }

    public static int? ResolvePreferredSeason(string rawPath, string fileName, int fallbackIndex = 0)
    {
        var parsed = ParseEpisodeInfo(fileName, fallbackIndex);
        if (parsed.IsTvShow)
        {
            return parsed.Season;
        }

        return ParsePreferredSeason(rawPath);
    }

    public static int? ParsePreferredSeason(string rawPath)
    {
        var match = Regex.Match(rawPath, @"[sS](\d{1,2})(?!\d)");
        if (match.Success)
        {
            return ParseOrDefault(match.Groups[1].Value, 0);
        }

        match = Regex.Match(rawPath, @"(?i)\bseason[\s._-]*(\d{1,2})\b");
        if (match.Success)
        {
            return ParseOrDefault(match.Groups[1].Value, 0);
        }

        match = Regex.Match(rawPath, @"第\s*(\d{1,2})\s*季");
        if (match.Success)
        {
            return ParseOrDefault(match.Groups[1].Value, 0);
        }

        return null;
    }

    public static string NormalizeTvSeriesTitle(string title)
    {
        var simplifiedTitle = ChineseTextNormalizer.NormalizeTitle(title);
        var normalized = Regex.Replace(simplifiedTitle, @"\s+", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return simplifiedTitle.Trim();
        }

        if (normalized.StartsWith("南家三姐妹", StringComparison.Ordinal))
        {
            return "南家三姐妹";
        }

        if (normalized.StartsWith("命运之夜前传", StringComparison.Ordinal))
        {
            return "命运之夜前传";
        }

        var aliasTitle = ResolveKnownSeriesAlias(normalized);
        if (aliasTitle is not null)
        {
            return aliasTitle;
        }

        return simplifiedTitle.Trim();
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
        var normalized = input.Replace('\\', '/');
        var candidates = new List<YearCandidate>();
        foreach (var component in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tokenMatches = Regex.Matches(component, @"(?:^|[^\p{L}\d])(19\d{2}|20\d{2})(?=$|[^\p{L}\d])");
            foreach (Match match in tokenMatches)
            {
                var year = match.Groups[1].Value;
                if (!int.TryParse(year, out var value) || value is < 1900 or > 2099)
                {
                    continue;
                }

                var before = component[..match.Groups[1].Index];
                var isLeadingToken = !Regex.IsMatch(before, @"[\p{L}\d]");
                candidates.Add(new YearCandidate(year, isLeadingToken));
            }
        }

        return candidates
                   .Where(static candidate => !candidate.IsLeadingToken)
                   .Select(static candidate => candidate.Year)
                   .LastOrDefault()
               ?? candidates.Select(static candidate => candidate.Year).LastOrDefault();
    }

    private static string PreferTitleTextBeforeReleaseYear(string input, string year)
    {
        var index = input.IndexOf(year, StringComparison.Ordinal);
        return index > 0 ? input[..index] : input;
    }

    private static string TruncateBeforeReleaseMetadata(string input)
    {
        var normalized = Regex.Replace(input, @"[._]+", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return input;
        }

        var endIndex = tokens.Length;
        for (var index = 0; index < tokens.Length; index++)
        {
            if (IsReleaseMetadataToken(tokens[index]))
            {
                endIndex = index;
                break;
            }
        }

        return endIndex > 0 ? string.Join(' ', tokens.Take(endIndex)) : normalized;
    }

    private static string? ExtractChineseTitle(IReadOnlyList<string> tokens)
    {
        var genericChineseTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "电影", "電影", "电影版", "電影版", "剧场版", "劇場版", "映画", "完全版", "总集篇", "總集篇",
            "特别篇", "特別篇", "花絮", "幕后", "幕後", "特典", "附赠", "附贈", "预告片", "預告片", "样片", "樣片"
        };
        var groups = new List<string>();
        var current = new List<string>();

        foreach (var token in tokens)
        {
            var isNumericSuffix = current.Count > 0 && Regex.IsMatch(token, @"^\d+$");
            if (ContainsHan(token) && !genericChineseTokens.Contains(token.Trim()))
            {
                current.Add(token);
                continue;
            }

            if (isNumericSuffix)
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

        var merged = groups.OrderByDescending(static x => x.Length).FirstOrDefault(static x => x.Length > 0);
        return string.IsNullOrWhiteSpace(merged)
            ? null
            : ChineseTextNormalizer.NormalizeTitle(TrimChineseSupplementTitle(merged));
    }

    private static string? UsableDisplayTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = Regex.Replace(value, @"[._]+", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "download",
            "downloads",
            "items",
            "library",
            "libraries",
            "media",
            "movie",
            "movies",
            "film",
            "films",
            "show",
            "shows",
            "series",
            "tv",
            "video",
            "videos",
            "stream",
            "bdmv",
            "plex",
            "emby",
            "jellyfin"
        };
        if (blocked.Contains(normalized))
        {
            return null;
        }

        if (normalized.All(char.IsDigit))
        {
            var compact = normalized.Trim('0');
            if (normalized.Length < 3 || compact.Length == 0)
            {
                return null;
            }
        }

        return Regex.IsMatch(normalized, @"[A-Za-z\p{IsCJKUnifiedIdeographs}\d]")
            ? normalized
            : null;
    }

    private static string? NonEmpty(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string? ResolveKnownSeriesAlias(string normalizedTitle)
    {
        var key = Regex.Replace(normalizedTitle, @"[._\-:：'’&+]+", string.Empty).ToLowerInvariant();
        if (key.StartsWith("hayatenogotoku", StringComparison.Ordinal)
            || key.StartsWith("hayatethecombatbutler", StringComparison.Ordinal))
        {
            return "旋风管家";
        }

        return key switch
        {
            "strangerthings" => "怪奇物语",
            "theglory" => "黑暗荣耀",
            _ => null
        };
    }

    private sealed record YearCandidate(string Year, bool IsLeadingToken);

    private static bool LooksLikeRawReleaseName(string value)
    {
        return Regex.IsMatch(
            value,
            @"(?i)(\b(480p|720p|1080p|2160p|4k|uhd|blu[- ]?ray|bluray|bdrip|web[- ]?dl|webrip|remux|x264|x265|h\.?264|h\.?265|hevc|truehd|atmos|dts|hdr|dv)\b|[._]{2,})");
    }

    private static bool IsLikelyMediaServerEndpointPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        return Regex.IsMatch(normalized, @"^(items/[^/]+/download|library/parts/|library/metadata/|video/|videos/)");
    }

    private static string? ExtractForeignTitle(IReadOnlyList<string> tokens)
    {
        var noiseTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "tx", "mweb", "adweb", "web", "dl", "bluray", "bdrip", "webrip",
            "proper", "repack", "remastered", "extended", "unrated",
            "vol", "volume", "disc", "disk", "cd", "part", "bonus", "extra", "extras",
            "featurette", "trailer", "sample", "cctv", "cctv4k", "cmctv", "cntv", "nhk", "wowow", "bdrom", "bdmv", "special", "features", "anniversary", "edition",
            "avc", "vc1", "lpcm", "truehd", "ma", "usa", "ger", "gbr", "uk", "jpn", "jap", "kor", "chn", "hkg", "tw"
        };
        var leadingNumericTitleIndexes = new HashSet<int>();
        for (var index = 0; index < tokens.Count; index++)
        {
            if (!Regex.IsMatch(tokens[index], @"^\d{1,2}$"))
            {
                break;
            }

            leadingNumericTitleIndexes.Add(index);
        }

        if (leadingNumericTitleIndexes.Count < 2)
        {
            leadingNumericTitleIndexes.Clear();
        }

        var numericSuffixTitleIndexes = new HashSet<int>();
        for (var index = 1; index < tokens.Count; index++)
        {
            if (Regex.IsMatch(tokens[index], @"^\d{1,3}$") && Regex.IsMatch(tokens[index - 1], @"\p{L}"))
            {
                numericSuffixTitleIndexes.Add(index);
            }
        }

        var filtered = tokens
            .Select((token, index) => new { Token = token, Index = index })
            .Where(static item => !ContainsHan(item.Token))
            .Where(item => !noiseTokens.Contains(item.Token))
            .Where(item => !item.Token.StartsWith("-", StringComparison.Ordinal) && !item.Token.EndsWith("-", StringComparison.Ordinal))
            .Where(item => !Regex.IsMatch(item.Token, @"^\d+$")
                           || leadingNumericTitleIndexes.Contains(item.Index)
                           || numericSuffixTitleIndexes.Contains(item.Index))
            .Where(static item => !Regex.IsMatch(item.Token, @"^(disc|disk|cd|dvd)$", RegexOptions.IgnoreCase))
            .Where(static item => !Regex.IsMatch(item.Token, @"^(disc|disk|cd|dvd)[-_ ]?\d{1,2}$", RegexOptions.IgnoreCase))
            .Where(static item => !Regex.IsMatch(item.Token, @"^(vol|volume)[-_ ]?\d{1,2}([\-–]\d{1,2})?$", RegexOptions.IgnoreCase))
            .Where(static item => !Regex.IsMatch(item.Token, @"^[se]\d{1,3}$", RegexOptions.IgnoreCase))
            .Where(static item => !Regex.IsMatch(item.Token, @"^(x|h)?26[45]$", RegexOptions.IgnoreCase))
            .Where(static item => !Regex.IsMatch(item.Token, @"^(aac|ac3|eac3|ddp|dts|truehd|flac|mp3)\d*(\.\d+)?$", RegexOptions.IgnoreCase))
            .Where(static item => !Regex.IsMatch(item.Token, @"^\d{1,2}bit$", RegexOptions.IgnoreCase))
            .Where(static item => Regex.IsMatch(item.Token, @"^(?=.*[\p{L}0-9])[\p{L}0-9'&:+-]+$"))
            .Select(static item => item.Token)
            .ToArray();

        var merged = string.Join(' ', filtered).Trim();
        if (string.IsNullOrWhiteSpace(merged))
        {
            return null;
        }

        var cleaned = TrimForeignSupplementTitle(merged);
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
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

    private static int FindPathComponent(IReadOnlyList<string> components, string value)
    {
        for (var index = 0; index < components.Count; index++)
        {
            if (string.Equals(components[index], value, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsGenericDiscOrVolumeFolder(string input)
    {
        var token = Regex.Replace(input, @"[._\-\s]+", string.Empty).ToLowerInvariant();
        return Regex.IsMatch(token, @"^(vol(ume)?\d{0,2}|disc\d{0,2}|disk\d{0,2}|dvd\d{0,2}|cd\d{0,2}|bdrom|bdmv)$");
    }

    private static string MergeSplitTitleSegments(string previous, string current)
    {
        var prev = previous.Trim();
        var curr = current.Trim();
        if (string.IsNullOrWhiteSpace(prev))
        {
            return curr;
        }

        if (string.IsNullOrWhiteSpace(curr))
        {
            return prev;
        }

        if (IsGenericDiscOrVolumeFolder(prev) || IsReleaseMetadataToken(prev))
        {
            return curr;
        }

        var prevHasTitle = Regex.IsMatch(prev, @"[A-Za-z\p{IsCJKUnifiedIdeographs}]");
        var currHasTitle = Regex.IsMatch(curr, @"[A-Za-z\p{IsCJKUnifiedIdeographs}]");
        return prevHasTitle && currHasTitle ? $"{prev} {curr}" : curr;
    }

    private static bool IsReleaseMetadataToken(string token)
    {
        var lower = token.Trim().Trim('-', '_').ToLowerInvariant();
        return Regex.IsMatch(lower, @"^[se]\d{1,3}$")
               || Regex.IsMatch(lower, @"^s\d{1,2}e[p]?\d{1,3}$")
               || Regex.IsMatch(lower, @"^ep?\d{1,3}$")
               || Regex.IsMatch(lower, @"^\d{3,4}p$")
               || Regex.IsMatch(lower, @"^(4k|uhd|uhdtv|hdtv|hdr|hlg|dv|atmos|ddp\d*(\.\d+)?|dd\d*(\.\d+)?|aac\d*(\.\d+)?|ac3|eac3|dts|dts[- ]?hd|truehd|lpcm|flac|mp3)$")
               || Regex.IsMatch(lower, @"^(x|h)?26[45]$")
               || Regex.IsMatch(lower, @"^(web|webdl|webrip|bluray|blu-ray|bdrip|remux|avc|vc-?1)$")
               || Regex.IsMatch(lower, @"^(amzn|nf|netflix|dsnp|disney|hmax|max|atvp|appletv|hulu|cr)$")
               || Regex.IsMatch(lower, @"^(cctv\d*k?|cmctv|cntv|nhk|wowow|bs11|mbs)$")
               || Regex.IsMatch(lower, @"^(bonus|extra|extras|featurette|trailer|sample)$")
               || Regex.IsMatch(lower, @"^(disc|disk|cd|dvd)$")
               || Regex.IsMatch(lower, @"^(disc|disk|cd|dvd)[-_ ]?\d{1,2}$")
               || Regex.IsMatch(lower, @"^(bdrom|bdmv)$")
               || Regex.IsMatch(lower, @"^(vol|volume)[-_ ]?\d{1,2}([\-–]\d{1,2})?$");
    }

    private static bool IsEpisodeMarkerToken(string token)
    {
        return Regex.IsMatch(token, @"^s\d{1,2}e[p]?\d{1,3}$", RegexOptions.IgnoreCase)
               || Regex.IsMatch(token, @"^ep?\d{1,3}$", RegexOptions.IgnoreCase)
               || Regex.IsMatch(token, @"^第\s*\d{1,3}\s*[集话]$");
    }

    private static bool IsYearToken(string token)
    {
        return Regex.IsMatch(token, @"^(19\d{2}|20\d{2})$");
    }

    private static bool IsEpisodeSubtitleNoiseToken(string token)
    {
        return Regex.IsMatch(
            token,
            @"(?i)^(season|complete|proper|repack|remux|bluray|blu|ray|web|dl|webrip|hdtv|uhd|hdr|dv|hevc|avc|aac|dts|truehd|atmos|hdsweb|adweb|chdweb|hdsky|luckani)$");
    }

    private static string NormalizeSubtitleTokenKey(string token)
    {
        return Regex.Replace(token, @"[^\p{L}\d]+", string.Empty).ToLowerInvariant();
    }

    private static string TrimChineseSupplementTitle(string input)
    {
        var markers = new[] { "特典", "特別收錄", "特别收录", "特別收录", "花絮", "幕后", "幕後", "附赠", "附贈", "预告片", "預告片", "样片", "樣片" };
        foreach (var marker in markers)
        {
            var index = input.IndexOf(marker, StringComparison.Ordinal);
            if (index <= 0)
            {
                continue;
            }

            var prefix = input[..index].Trim();
            if (prefix.Length >= 2)
            {
                return prefix;
            }
        }

        return input;
    }

    private static string TrimForeignSupplementTitle(string input)
    {
        var result = input;
        result = Regex.Replace(result, @"\s+the\s+movie$", string.Empty, RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\s+main\s+feature$", string.Empty, RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\s+feature\s+film$", string.Empty, RegexOptions.IgnoreCase);
        return result.Trim();
    }

    private static string? ExtractBracketedChineseTitle(string text)
    {
        var match = Regex.Match(text, @"(?:\[|\(|【|（)\s*([\p{IsCJKUnifiedIdeographs}\d]{2,})\s*(?:\]|\)|】|）)");
        if (!match.Success)
        {
            return null;
        }

        var value = Regex.Replace(match.Groups[1].Value, @"\d{1,3}\s*周年\s*纪念版", string.Empty);
        value = Regex.Replace(value, @"(花絮|幕后花絮|幕后特辑|幕后|特典|附赠|预告片|样片)", string.Empty).Trim();
        return value.Length == 0 ? null : ChineseTextNormalizer.NormalizeTitle(value);
    }

    private static int ParseOrDefault(string input, int fallback)
    {
        return int.TryParse(input, out var value) ? value : fallback;
    }

    [GeneratedRegex(@"[\[\]\(\)\{\}【】（）《》「」『』〔〕〖〗]")]
    private static partial Regex BracketLikeCharactersRegex();
}
