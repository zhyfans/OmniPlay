using System.Text.RegularExpressions;
using OmniPlay.Core.Models.Playback;

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

    public static string CleanedTitleSource(string rawPath)
    {
        var normalized = rawPath.Replace('\\', '/');

        if (normalized.Contains("BDMV", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".m2ts", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".m2t", StringComparison.OrdinalIgnoreCase))
        {
            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var bdmvIndex = Array.FindIndex(parts, static x => x.Equals("BDMV", StringComparison.OrdinalIgnoreCase));
            if (bdmvIndex > 0)
            {
                var candidate = parts[bdmvIndex - 1];
                if (IsGenericDiscOrVolumeFolder(candidate) && bdmvIndex > 1)
                {
                    candidate = parts[bdmvIndex - 2];
                    if (bdmvIndex > 2)
                    {
                        candidate = MergeSplitTitleSegments(parts[bdmvIndex - 3], candidate);
                    }
                }
                else if (IsReleaseGroupWrapperFolder(candidate, bdmvIndex > 1 ? parts[bdmvIndex - 2] : string.Empty))
                {
                    candidate = parts[bdmvIndex - 2];
                    if (bdmvIndex > 2)
                    {
                        candidate = MergeSplitTitleSegments(parts[bdmvIndex - 3], candidate);
                    }
                }

                return NormalizeDiscTitleCandidate(candidate);
            }
        }

        if (SeasonRangeSeriesFolder(normalized) is { } packageFolder)
        {
            return packageFolder;
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

        var extractedYear = ChooseReleaseYear(originalText) ?? ChooseReleaseYear(textToParse);
        if (!string.IsNullOrWhiteSpace(extractedYear))
        {
            textToParse = PreferTitleTextBeforeReleaseYear(textToParse, extractedYear);
        }

        textToParse = RemoveDecorativeBracketSegments(textToParse);
        textToParse = RemoveReleaseTitleNoise(textToParse);
        var titleScopedText = textToParse;
        textToParse = BracketLikeCharactersRegex().Replace(textToParse, " ");

        foreach (var pattern in CleanupPatterns)
        {
            textToParse = Regex.Replace(textToParse, pattern, " ", RegexOptions.IgnoreCase);
        }

        textToParse = Regex.Replace(textToParse, @"[._]+", " ");
        textToParse = Regex.Replace(textToParse, @"\[[^\]]*\]|\([^\)]*\)", " ");
        textToParse = BracketLikeCharactersRegex().Replace(textToParse, " ");
        textToParse = Regex.Replace(textToParse, @"\s+", " ").Trim();

        var tokens = Tokenize(textToParse);
        var originalTokens = Tokenize(Regex.Replace(BracketLikeCharactersRegex().Replace(titleScopedText, " "), @"\s+", " ").Trim());

        var chineseTitle = ExtractChineseTitle(tokens) ?? ExtractChineseTitle(originalTokens);
        var foreignTitle = ExtractForeignTitle(tokens);

        return new SearchMetadata(
            chineseTitle,
            foreignTitle,
            ResolveFullCleanTitle(textToParse, chineseTitle, foreignTitle),
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

    public static string? ExtractedDisplayTitle(string relativePath, string fileName)
    {
        var metadata = CombinedSearchMetadata(relativePath, fileName);
        var candidates = new[]
        {
            metadata.ChineseTitle,
            metadata.ParentChineseTitle,
            metadata.ForeignTitle,
            metadata.FullCleanTitle
        };

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
            var partNumber = ParsePartNumberAfterMatch(fileName, seasonEpisodeMatch);
            var year = ExtractEpisodeYearAfterMatch(fileName, seasonEpisodeMatch);
            var subtitle = ExtractEpisodeSubtitleAfterMatch(fileName, seasonEpisodeMatch);
            return new EpisodeInfo(season, episode, BuildEpisodeDisplayName(episode, partNumber), true, partNumber, subtitle, year);
        }

        var episodeOnlyMatch = Regex.Match(fileName, @"[eE][pP]?(\d{1,3})");
        if (episodeOnlyMatch.Success)
        {
            episode = ParseOrDefault(episodeOnlyMatch.Groups[1].Value, episode);
            var partNumber = ParsePartNumberAfterMatch(fileName, episodeOnlyMatch);
            var year = ExtractEpisodeYearAfterMatch(fileName, episodeOnlyMatch);
            var subtitle = ExtractEpisodeSubtitleAfterMatch(fileName, episodeOnlyMatch);
            return new EpisodeInfo(season, episode, BuildEpisodeDisplayName(episode, partNumber), true, partNumber, subtitle, year);
        }

        return new EpisodeInfo(season, episode, fileName, false);
    }

    public static int? ParsePreferredSeason(string rawPath)
    {
        if (ParseMultiSeasonRangeStart(rawPath) is { } rangeStart && rangeStart > 0)
        {
            return rangeStart;
        }

        var match = Regex.Match(rawPath, @"[sS](\d{1,2})(?!\d)");
        if (match.Success)
        {
            return ParseOrDefault(match.Groups[1].Value, 0);
        }

        match = Regex.Match(rawPath, @"(?i)season[\s._-]*(\d{1,2})");
        if (match.Success)
        {
            return ParseOrDefault(match.Groups[1].Value, 0);
        }

        match = Regex.Match(rawPath, @"第\s*([一二三四五六七八九十零〇两\d]{1,3})\s*季");
        if (match.Success)
        {
            var text = match.Groups[1].Value;
            if (int.TryParse(text, out var numeric) && numeric > 0)
            {
                return numeric;
            }

            if (ChineseNumberToInt(text) is { } chineseNumber && chineseNumber > 0)
            {
                return chineseNumber;
            }
        }

        return null;
    }

    public static int? ResolvePreferredSeason(string rawPath, string fileName, int fallbackIndex = 0)
    {
        if (ParseMultiSeasonRangeStart(rawPath) is { } rangeStart && rangeStart > 0)
        {
            return rangeStart;
        }

        var parsed = ParseEpisodeInfo(fileName, fallbackIndex);
        return parsed.IsTvShow && parsed.Season > 0 ? parsed.Season : ParsePreferredSeason(rawPath);
    }

    public static int? ParseMultiSeasonRangeStart(string rawPath)
    {
        var components = rawPath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var searchable = components.Length > 1 ? components.Take(components.Length - 1) : components;
        foreach (var component in searchable.Reverse())
        {
            if (SeasonRangeStart(component) is { } value && value > 0)
            {
                return value;
            }
        }

        return null;
    }

    private static int? SeasonRangeStart(string text)
    {
        var match = Regex.Match(text, @"(?i)\bS(?<start>\d{1,2})\s*[-–~]\s*(?:S)?\d{1,2}\b");
        if (match.Success)
        {
            return ParseOrDefault(match.Groups["start"].Value, 0);
        }

        match = Regex.Match(text, @"(?i)\bseason\s*(?<start>\d{1,2})\s*[-–~]\s*(?:season\s*)?\d{1,2}\b");
        if (match.Success)
        {
            return ParseOrDefault(match.Groups["start"].Value, 0);
        }

        match = Regex.Match(text, @"第\s*(?<start>[一二三四五六七八九十零〇两\d]{1,3})\s*[-–~至到]\s*[一二三四五六七八九十零〇两\d]{1,3}\s*季");
        if (match.Success)
        {
            var start = match.Groups["start"].Value;
            if (int.TryParse(start, out var numeric))
            {
                return numeric;
            }

            return ChineseNumberToInt(start);
        }

        return null;
    }

    private static string? SeasonRangeSeriesFolder(string rawPath)
    {
        var components = rawPath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (components.Length == 0)
        {
            return null;
        }

        var folderComponents = components.Length > 1 ? components.Take(components.Length - 1) : components;
        foreach (var component in folderComponents.Reverse())
        {
            if (LooksLikeSeasonFolderComponent(component))
            {
                continue;
            }

            if (SeasonRangeStart(component) is not null)
            {
                return component;
            }
        }

        return null;
    }

    private static bool LooksLikeSeasonFolderComponent(string value)
    {
        return Regex.IsMatch(value, @"(?i)^(season|s)\s*[\._-]?\s*\d{1,2}$") ||
               Regex.IsMatch(value, @"^第\s*[一二三四五六七八九十零〇两\d]{1,3}\s*季$");
    }

    public static bool IsLikelyTvEpisodePath(string rawPath)
    {
        var lower = rawPath.ToLowerInvariant();
        return Regex.IsMatch(lower, @"[s]\d{1,2}[e][p]?\d{1,3}")
               || Regex.IsMatch(lower, @"\bep?\d{1,3}\b")
               || Regex.IsMatch(lower, @"\bs\d{1,2}\b")
               || Regex.IsMatch(lower, @"\bseason[\s._-]*\d{1,2}\b")
               || Regex.IsMatch(rawPath, @"第\s*\d{1,3}\s*[集话]")
               || Regex.IsMatch(rawPath, @"第\s*[一二三四五六七八九十零〇两\d]{1,3}\s*季");
    }

    public static bool IsLikelyMoviePath(string rawPath)
    {
        var lower = rawPath.Replace('\\', '/').ToLowerInvariant();
        if (lower.Contains("/bdmv/") || lower.EndsWith(".iso") || lower.EndsWith(".m2ts") || lower.EndsWith(".m2t"))
        {
            return true;
        }

        var fileName = Path.GetFileName(lower);
        return !string.IsNullOrWhiteSpace(fileName) &&
               Regex.IsMatch(fileName, @"(disc|disk|dvd|cd|vol|volume)[-_ ]?\d{0,2}");
    }

    public static (int Season, int Episode, int Part, string FileName) EpisodeSortKey(string fileName, int fallbackIndex)
    {
        var parsed = ParseEpisodeInfo(fileName, fallbackIndex);
        var seasonOrder = parsed.Season == 0 ? int.MaxValue : parsed.Season;
        return (seasonOrder, parsed.Episode, parsed.PartNumber ?? 0, fileName);
    }

    private static readonly string[] CleanupPatterns =
    [
        @"\b(1080p|2160p|4k|720p|480p|uhd|uhdtv|hdtv|blu[- ]?ray|bluray|bdrip|web[- ]?dl|webdl|webrip|remux|x264|x265|h\.?264|h\.?265|hevc|avc|vc[- ]?1|aac|dts[- ]?hd|dts[- ]?x|dtsx|dts|ddp|lpcm|truehd|atmos|hdr|hlg|dv)\b",
        @"\b[sS]\d{1,2}\s*[-–~]\s*(?:[sS])?\d{1,2}\b",
        @"\bseason\s*\d{1,2}\s*[-–~]\s*(?:season\s*)?\d{1,2}\b",
        @"\b[sS]\d{1,2}[eE][pP]?\d{1,3}\b",
        @"\b[sS]\d{1,2}\b",
        @"\b[eE][pP]?\d{1,3}\b",
        @"第\s*\d{1,3}\s*[集话]",
        @"\bepisode[\s._-]*\d{1,3}\b",
        @"\b(?:part|pt)[\s._-]*\d{1,2}\b",
        @"\bseason\s*\d{1,2}\b",
        @"\bcomplete\b",
        @"\b(cctv4k|cctv)\b",
        @"\b\d{1,2}bit\b",
        @"\b(aac|ac3|eac3|ddp|dts|dts[- ]?hd|truehd|lpcm|flac|mp3)\d*(\.\d+)?\b",
        @"\b(ma|hi10p|10bit|8bit)\b",
        @"\b(usa|ger|gbr|uk|jpn|jap|kor|chn|hkg|tw|fr|fra|ita|esp|rus|can|aus)\b",
        @"\b(bonus|extras?|featurette|behind[- ]?the[- ]?scenes|trailer|sample)\b",
        @"\b(disc|disk|cd|dvd)\b",
        @"\b(disc|disk|cd|dvd)\s*[-_ ]?\d{1,2}\b",
        @"\b(bdrom|bdmv)\b",
        @"\b(vol|volume)\s*[-_ ]?\d{1,2}([\-–]\d{1,2})?\b",
        @"\d{1,3}\s*周年\s*纪念版",
        @"(映画|剧场版|劇場版|電影版|电影版|完全版|总集篇|總集篇|特別篇|特别篇)",
        @"\b(special\s*features?|featurettes?)\b",
        @"\b\d{1,3}(st|nd|rd|th)\s+anniversary(\s+edition)?\b",
        @"\b(anniversary|edition)\b",
        @"(纪念版|花絮|特典|番外|幕后花絮|幕后特辑)"
    ];

    private static string? ExtractChineseTitle(IReadOnlyList<string> tokens)
    {
        var genericChineseTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "电影", "電影", "电影版", "電影版", "剧场版", "劇場版", "映画", "完全版", "总集篇", "總集篇",
            "特别篇", "特別篇", "花絮", "幕后", "幕後", "特典", "附赠", "附贈", "预告片", "預告片", "样片", "樣片"
        };
        List<string>? current = null;
        List<List<string>> groups = [];

        foreach (var token in tokens)
        {
            var hasHan = ContainsHan(token);
            var normalizedToken = token.Trim();
            var isGenericChineseToken = genericChineseTokens.Contains(normalizedToken);
            var isNumericSuffix = current is { Count: > 0 } && Regex.IsMatch(token, @"^\d+$");
            if (hasHan && !isGenericChineseToken)
            {
                current ??= [];
                current.Add(token);
            }
            else if (isNumericSuffix)
            {
                current!.Add(token);
            }
            else if (current is { Count: > 0 })
            {
                groups.Add(current);
                current = null;
            }
        }

        if (current is { Count: > 0 })
        {
            groups.Add(current);
        }

        return groups
            .OrderByDescending(static x => string.Concat(x).Length)
            .Select(static x => string.Concat(x).Trim())
            .Select(TrimChineseSupplementTitle)
            .FirstOrDefault(static x => x.Length > 0);
    }

    private static string TrimChineseSupplementTitle(string input)
    {
        string[] markers = ["特典", "特別收錄", "特别收录", "特別收录", "花絮", "幕后", "幕後", "附赠", "附贈", "预告片", "預告片", "样片", "樣片"];
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

    private static string? ResolveFullCleanTitle(string textToParse, string? chineseTitle, string? foreignTitle)
    {
        if (!string.IsNullOrWhiteSpace(chineseTitle))
        {
            return chineseTitle.Trim();
        }

        if (!string.IsNullOrWhiteSpace(textToParse))
        {
            return textToParse.Trim();
        }

        return string.IsNullOrWhiteSpace(foreignTitle) ? null : foreignTitle.Trim();
    }

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
            "featurette", "trailer", "sample", "cmctv", "bdrom", "bdmv", "special", "features",
            "anniversary", "edition", "complete", "episode", "cctv", "cctv4k",
            "avc", "vc1", "lpcm", "truehd", "ma", "cmct", "hdsky", "hds", "luckdiy", "x-man",
            "usa", "ger", "gbr", "uk", "jpn", "jap", "kor", "chn", "hkg", "tw"
        };
        var leadingNumericTitleIndexes = tokens
            .TakeWhile(static token => Regex.IsMatch(token, @"^\d{1,2}$"))
            .Select((_, index) => index)
            .ToHashSet();
        if (leadingNumericTitleIndexes.Count < 2)
        {
            leadingNumericTitleIndexes.Clear();
        }

        var filtered = tokens.Select((token, index) => new { token, index }).Where(item =>
        {
            var token = item.token;
            var lower = token.ToLowerInvariant();
            if (noiseTokens.Contains(lower)) return false;
            if (lower.StartsWith('-') || lower.EndsWith('-')) return false;
            if (ContainsHan(token)) return false;
            if (!Regex.IsMatch(token, @"^(?=.*[\p{L}0-9])[\p{L}0-9'&:+-]+$")) return false;
            if (Regex.IsMatch(lower, @"^\d+$") && !leadingNumericTitleIndexes.Contains(item.index)) return false;
            if (Regex.IsMatch(lower, @"^(disc|disk|cd|dvd)$")) return false;
            if (Regex.IsMatch(lower, @"^(disc|disk|cd|dvd)[-_ ]?\d{1,2}$")) return false;
            if (Regex.IsMatch(lower, @"^(vol|volume)[-_ ]?\d{1,2}([\-–]\d{1,2})?$")) return false;
            if (Regex.IsMatch(lower, @"^[se]\d{1,3}$")) return false;
            if (Regex.IsMatch(lower, @"^(part|pt)\d{1,2}$")) return false;
            if (Regex.IsMatch(lower, @"^(x|h)?26[45]$")) return false;
            if (Regex.IsMatch(lower, @"^(aac|ac3|eac3|ddp|dts|truehd|flac|mp3)\d*(\.\d+)?$")) return false;
            if (Regex.IsMatch(lower, @"^\d{1,2}bit$")) return false;
            if (Regex.IsMatch(lower, @"^cctv\d*k?$")) return false;
            return true;
        }).Select(static item => item.token);

        var merged = string.Join(' ', filtered).Trim();
        if (merged.Length == 0)
        {
            return null;
        }

        var cleanedMerged = TrimForeignSupplementTitle(merged);
        var lowered = cleanedMerged.ToLowerInvariant().Trim();
        if (Regex.IsMatch(lowered, @"^(vol|volume|disc|disk|cd|part)\s*\d*$"))
        {
            return null;
        }

        return cleanedMerged.Length == 0 ? null : cleanedMerged;
    }

    private static string TrimForeignSupplementTitle(string input)
    {
        var result = Regex.Replace(input, @"(?i)\s+the\s+movie$", string.Empty);
        result = Regex.Replace(result, @"(?i)\s+main\s+feature$", string.Empty);
        result = Regex.Replace(result, @"(?i)\s+feature\s+film$", string.Empty);
        return result.Trim();
    }

    private static string TruncateBeforeReleaseMetadata(string input)
    {
        var normalized = Regex.Replace(input, @"[._]+", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        normalized = RemoveDecorativeBracketSegments(normalized);
        var tokens = Tokenize(normalized);
        if (tokens.Count == 0)
        {
            return input;
        }

        var endIndex = tokens.Count;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (IsReleaseMetadataToken(tokens[i]))
            {
                endIndex = i;
                break;
            }
        }

        return endIndex <= 0 ? normalized : string.Join(' ', tokens.Take(endIndex));
    }

    private static string NormalizeDiscTitleCandidate(string candidate)
    {
        return RemoveReleaseTitleNoise(candidate);
    }

    private static string RemoveDecorativeBracketSegments(string input)
    {
        return Regex.Replace(
            input,
            @"[\[\(\{（【]\s*[^]\)\}）】]*(?:剧场版|紀念版|纪念版|花絮|特典|番外|幕后花絮|幕后特辑|anniversary|edition|featurette|bonus|extra|extras|trailer)[^]\)\}）】]*[\]\)\}）】]",
            " ",
            RegexOptions.IgnoreCase);
    }

    private static string RemoveReleaseTitleNoise(string input)
    {
        var normalized = Regex.Replace(input, @"[._]+", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        var supplementTrimmed = TrimChineseSupplementTitle(normalized);
        if (!string.Equals(supplementTrimmed, normalized, StringComparison.Ordinal))
        {
            return supplementTrimmed;
        }

        normalized = Regex.Replace(
            normalized,
            @"(?i)^(?:disc|disk|cd|dvd|vol|volume)\s*\d{0,2}\s*[-–—:]+\s*",
            string.Empty);
        normalized = Regex.Replace(normalized, @"^[\s._\-–—:]+", string.Empty);

        normalized = Regex.Replace(normalized, @"(?i)\b(?:cctv4k|cctv)\b", " ");
        normalized = Regex.Replace(normalized, @"(?i)\bepisode\s*\d{1,3}\b", " ");
        normalized = Regex.Replace(normalized, @"(?:剧场版|劇場版|电影版|電影版|映画|完全版|总集篇|總集篇|特别篇|特別篇|纪念版|花絮|特典|番外|幕后花絮|幕后特辑)", " ");

        var withoutMovieSuffix = Regex.Replace(
            normalized,
            @"(?i)(?:\s*[-–—:]\s*|\s+)(the\s+movie|main\s+feature|feature\s+film)\s*$",
            string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(withoutMovieSuffix))
        {
            normalized = withoutMovieSuffix;
        }

        foreach (var pattern in CleanupPatterns)
        {
            normalized = Regex.Replace(normalized, pattern, " ", RegexOptions.IgnoreCase);
        }

        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static string PreferTitleTextBeforeReleaseYear(string input, string year)
    {
        var matches = Regex.Matches(input, $@"(?<!\d){Regex.Escape(year)}(?!\d)");
        foreach (Match match in matches)
        {
            var prefix = input[..match.Index];
            if (!string.IsNullOrWhiteSpace(NormalizeTitleSegment(prefix)))
            {
                return prefix;
            }
        }

        return Regex.Replace(
            input,
            $@"[ \.\-_\(\)\[\]\{{\}}]*{Regex.Escape(year)}[ \.\-_\(\)\[\]\{{\}}]*",
            " ",
            RegexOptions.IgnoreCase);
    }

    private static string NormalizeTitleSegment(string input)
    {
        var normalized = BracketLikeCharactersRegex().Replace(input, " ");
        normalized = Regex.Replace(normalized, @"[._]+", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized.Trim(' ', '.', '-', '_', '/', '\\', '(', ')', '[', ']', '{', '}');
    }

    private static bool IsReleaseMetadataToken(string token)
    {
        var lower = token.ToLowerInvariant();
        return Regex.IsMatch(lower, @"^[se]\d{1,3}$")
               || Regex.IsMatch(lower, @"^s\d{1,2}e[p]?\d{1,3}$")
               || Regex.IsMatch(lower, @"^ep?\d{1,3}$")
               || Regex.IsMatch(lower, @"^\d{3,4}p$")
               || Regex.IsMatch(lower, @"^(4k|uhd|uhdtv|hdtv|hdr|hlg|dv|atmos|ddp\d*(\.\d+)?|aac\d*(\.\d+)?|ac3|eac3|dts|dts[- ]?hd|dts[- ]?x|dtsx|truehd|lpcm|flac|mp3)$")
               || Regex.IsMatch(lower, @"^(x|h)?26[45]$")
               || Regex.IsMatch(lower, @"^(web|web-?dl|webdl|webrip|bluray|blu-ray|bdrip|remux|avc|vc-?1)$")
               || Regex.IsMatch(lower, @"^(amzn|nf|netflix|dsnp|disney|hmax|max|atvp|appletv|hulu|cr)$")
               || Regex.IsMatch(lower, @"^(flux|ntb|cakes|tgx|successfulcrab)$")
               || Regex.IsMatch(lower, @"^(bonus|extra|extras|featurette|trailer|sample)$")
               || Regex.IsMatch(lower, @"^(part|pt)\d{1,2}$")
               || Regex.IsMatch(lower, @"^(disc|disk|cd|dvd)$")
               || Regex.IsMatch(lower, @"^(disc|disk|cd|dvd)[-_ ]?\d{0,2}$")
               || Regex.IsMatch(lower, @"^(bdrom|bdmv)$")
               || Regex.IsMatch(lower, @"^(vol|volume)[-_ ]?\d{0,2}([\-–]\d{1,2})?$");
    }

    private static string? ChooseReleaseYear(string text)
    {
        var candidates = YearRegex().Matches(text)
            .Select(static x => x.Value)
            .Distinct()
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count >= 2)
        {
            var compactPrefix = Regex.Replace(text[..Math.Min(text.Length, 12)], @"[.\-_/\s]+", string.Empty);
            if (compactPrefix.StartsWith(candidates[0], StringComparison.Ordinal))
            {
                return candidates[1];
            }
        }

        return candidates[0];
    }

    private static string MergeSplitTitleSegments(string previous, string current)
    {
        var prev = previous.Trim();
        var curr = current.Trim();
        if (prev.Length == 0) return curr;
        if (curr.Length == 0) return prev;
        if (IsGenericDiscOrVolumeFolder(prev) || IsReleaseMetadataToken(prev)) return curr;
        if (!Regex.IsMatch(prev, @"[A-Za-z\p{IsCJKUnifiedIdeographs}]")) return curr;
        if (!Regex.IsMatch(curr, @"[A-Za-z\p{IsCJKUnifiedIdeographs}]")) return curr;
        return $"{prev} {curr}";
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
        return value.Length == 0 ? null : value;
    }

    private static bool IsGenericDiscOrVolumeFolder(string input)
    {
        var token = Regex.Replace(input, @"[._\-\s]+", string.Empty).ToLowerInvariant();
        return Regex.IsMatch(token, @"^(vol(ume)?\d{0,2}|disc\d{0,2}|disk\d{0,2}|dvd\d{0,2}|cd\d{0,2}|bdrom|bdmv)$");
    }

    private static bool IsReleaseGroupWrapperFolder(string input, string ancestor)
    {
        if (string.IsNullOrWhiteSpace(input) ||
            string.IsNullOrWhiteSpace(ancestor) ||
            ContainsHan(input) ||
            ChooseReleaseYear(input) is not null ||
            !HasLikelyTitleSignal(ancestor))
        {
            return false;
        }

        var tokens = Regex
            .Split(input.Trim(), @"[\s._\-@#]+")
            .Where(static token => !string.IsNullOrWhiteSpace(token))
            .ToArray();
        return tokens.Length >= 2 && tokens.All(IsLikelyReleaseGroupToken);
    }

    private static bool HasLikelyTitleSignal(string input)
    {
        return ContainsHan(input) ||
               ChooseReleaseYear(input) is not null ||
               ExtractBracketedChineseTitle(input) is not null;
    }

    private static bool IsLikelyReleaseGroupToken(string token)
    {
        return Regex.IsMatch(token, @"^[A-Z0-9]{2,10}$", RegexOptions.CultureInvariant);
    }

    private static List<string> Tokenize(string input)
    {
        return input.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static int ParseOrDefault(string value, int fallback)
    {
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static int? ParsePartNumberAfterMatch(string fileName, Match episodeMatch)
    {
        var tailStart = episodeMatch.Index + episodeMatch.Length;
        if (tailStart >= fileName.Length)
        {
            return null;
        }

        var tail = fileName[tailStart..];
        var match = Regex.Match(
            tail,
            @"(?i)(?:^|[\s._\-\(\[])(?:part|pt)[\s._-]*(\d{1,2})(?=$|[\s._\-\)\]])");
        if (!match.Success)
        {
            return null;
        }

        var part = ParseOrDefault(match.Groups[1].Value, 0);
        return part > 0 ? part : null;
    }

    private static string? ExtractEpisodeSubtitleAfterMatch(string fileName, Match episodeMatch)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var tailStart = episodeMatch.Index + episodeMatch.Length;
        if (tailStart >= stem.Length)
        {
            return null;
        }

        var tail = stem[tailStart..];
        tail = Regex.Replace(
            tail,
            @"(?i)^(?:[\s._\-\(\[])*(?:part|pt)[\s._-]*\d{1,2}(?=$|[\s._\-\)\]])",
            string.Empty);
        tail = Regex.Replace(tail, @"^[\s._\-\)\]\(\[]+", string.Empty);
        tail = Regex.Replace(tail, @"[\s._\-\(\[]+$", string.Empty);
        if (string.IsNullOrWhiteSpace(tail))
        {
            return null;
        }

        var normalized = Regex.Replace(tail, @"[._-]+", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        var tokens = Tokenize(normalized);
        if (tokens.Count == 0 || tokens.All(static token => IsReleaseMetadataToken(token) || IsEpisodeYearToken(token)))
        {
            return null;
        }

        tokens = tokens
            .SkipWhile(static token => IsEpisodeYearToken(token))
            .ToList();

        var keptTokens = tokens
            .TakeWhile(static token => !IsReleaseMetadataToken(token) && !IsEpisodeYearToken(token))
            .ToList();

        var subtitle = string.Join(' ', keptTokens).Trim();
        return subtitle.Length == 0 ? null : subtitle;
    }

    private static string? ExtractEpisodeYearAfterMatch(string fileName, Match episodeMatch)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var tailStart = episodeMatch.Index + episodeMatch.Length;
        if (tailStart >= stem.Length)
        {
            return null;
        }

        var tail = stem[tailStart..];
        var match = YearRegex().Match(tail);
        return match.Success ? match.Value : null;
    }

    private static string BuildEpisodeDisplayName(int episode, int? partNumber)
    {
        return partNumber.HasValue
            ? $"Episode {episode} Part {partNumber.Value}"
            : $"Episode {episode}";
    }

    private static bool ContainsHan(string value)
    {
        return HanRegex().IsMatch(value);
    }

    private static bool IsEpisodeYearToken(string token)
    {
        return Regex.IsMatch(token, @"^(19\d{2}|20\d{2})$");
    }

    private static int? ChineseNumberToInt(string input)
    {
        var text = input.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        Dictionary<char, int> map = new()
        {
            ['零'] = 0,
            ['〇'] = 0,
            ['一'] = 1,
            ['二'] = 2,
            ['两'] = 2,
            ['三'] = 3,
            ['四'] = 4,
            ['五'] = 5,
            ['六'] = 6,
            ['七'] = 7,
            ['八'] = 8,
            ['九'] = 9
        };

        if (text == "十")
        {
            return 10;
        }

        if (text.StartsWith('十'))
        {
            var ones = text.Length > 1 && map.TryGetValue(text[1], out var mapped) ? mapped : 0;
            return 10 + ones;
        }

        var tenIndex = text.IndexOf('十');
        if (tenIndex > 0)
        {
            var tens = map.TryGetValue(text[0], out var mappedTens) ? mappedTens : 0;
            var ones = tenIndex + 1 < text.Length && map.TryGetValue(text[tenIndex + 1], out var mappedOnes)
                ? mappedOnes
                : 0;
            return tens * 10 + ones;
        }

        return text.Length == 1 && map.TryGetValue(text[0], out var value) ? value : null;
    }

    [GeneratedRegex(@"\p{IsCJKUnifiedIdeographs}")]
    private static partial Regex HanRegex();

    [GeneratedRegex(@"[\[\]\(\)\{\}（）【】]")]
    private static partial Regex BracketLikeCharactersRegex();

    [GeneratedRegex(@"(?<!\d)(19\d{2}|20\d{2})(?!\d)")]
    private static partial Regex YearRegex();
}
