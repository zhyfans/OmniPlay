using System.Text;
using System.Text.RegularExpressions;
using OmniPlay.Core.Models.Entities;

namespace OmniPlay.Infrastructure.Library;

public static class LibraryLookupTitleBuilder
{
    public static List<string> Build(
        string currentTitle,
        string? sourceProtocolType,
        string? baseUrl,
        string? relativePath,
        string? manualQuery = null,
        string? fileName = null)
    {
        List<string> titles = [];

        AddIfPresent(titles, manualQuery);

        var isMediaServer = IsMediaServerProtocol(sourceProtocolType);
        if (isMediaServer)
        {
            AddSearchMetadataCandidates(titles, fileName, includeParentFolder: false);
        }
        else if (!string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(relativePath))
        {
            var metadataPath = MediaSourcePathResolver.ResolveMetadataPath(
                sourceProtocolType,
                baseUrl,
                relativePath);
            AddSearchMetadataCandidates(titles, metadataPath, includeParentFolder: true);
            AddSearchMetadataCandidates(titles, fileName, includeParentFolder: false);
        }
        else if (!string.IsNullOrWhiteSpace(fileName))
        {
            AddSearchMetadataCandidates(titles, fileName, includeParentFolder: false);
        }

        AddCurrentTitleCandidate(titles, currentTitle, fileName);

        return DeduplicateTitles(titles);
    }

    private static void AddSearchMetadataCandidates(List<string> titles, string? rawPath, bool includeParentFolder)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return;
        }

        var metadata = MediaNameParser.ExtractSearchMetadata(rawPath);
        var parentChineseTitle = includeParentFolder
            ? MediaNameParser.ExtractParentFolderChineseTitle(rawPath)
            : null;

        AddIfPresent(titles, metadata.ChineseTitle);
        AddIfPresent(titles, parentChineseTitle);

        foreach (var foreignQuery in BuildForeignQueryCandidates(metadata.ForeignTitle))
        {
            AddIfPresent(titles, foreignQuery);
        }

        AddIfPresent(titles, metadata.FullCleanTitle);
        AddIfPresent(titles, BuildPureNameFallback(metadata.FullCleanTitle));
    }

    private static bool IsMediaServerProtocol(string? sourceProtocolType)
    {
        return Enum.TryParse<MediaSourceProtocol>(sourceProtocolType, ignoreCase: true, out var protocol) &&
               protocol is MediaSourceProtocol.Plex or MediaSourceProtocol.Emby or MediaSourceProtocol.Jellyfin;
    }

    private static void AddCurrentTitleCandidate(List<string> titles, string? currentTitle, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(currentTitle))
        {
            return;
        }

        var trimmed = currentTitle.Trim();
        var normalized = NormalizeTitle(trimmed);
        if (normalized is "download" or "items")
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var fileStem = Path.GetFileNameWithoutExtension(fileName);
            if (normalized == NormalizeTitle(fileName) || normalized == NormalizeTitle(fileStem))
            {
                AddSearchMetadataCandidates(titles, fileName, includeParentFolder: false);
                return;
            }
        }

        if (LooksLikeRawReleaseName(trimmed))
        {
            AddSearchMetadataCandidates(titles, trimmed, includeParentFolder: false);
            return;
        }

        AddIfPresent(titles, trimmed);
    }

    private static bool LooksLikeRawReleaseName(string value)
    {
        return Regex.IsMatch(
            value,
            @"(?i)(\b(480p|720p|1080p|2160p|4k|uhd|blu[- ]?ray|bluray|bdrip|web[- ]?dl|webrip|remux|x264|x265|h\.?264|h\.?265|hevc|truehd|atmos|dts|hdr|dv)\b|[._]{2,})");
    }

    private static IReadOnlyList<string> BuildForeignQueryCandidates(string? foreignTitle)
    {
        var trimmed = foreignTitle?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return [];
        }

        List<string> candidates = [trimmed];
        var normalized = Regex.Replace(trimmed, @"\bA\.?K\.?A\.?\b", "AKA", RegexOptions.IgnoreCase);
        normalized = normalized
            .Replace("(AKA)", "AKA", StringComparison.OrdinalIgnoreCase)
            .Replace("（AKA）", "AKA", StringComparison.OrdinalIgnoreCase)
            .Replace(" aka ", " AKA ", StringComparison.OrdinalIgnoreCase);
        if (normalized.Contains("AKA", StringComparison.Ordinal))
        {
            candidates.AddRange(normalized
                .Split("AKA", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return DeduplicateTitles(candidates);
    }

    private static string? BuildPureNameFallback(string? fullCleanTitle)
    {
        if (string.IsNullOrWhiteSpace(fullCleanTitle))
        {
            return null;
        }

        var fallback = Regex.Replace(fullCleanTitle, @"[\s\d]+", string.Empty).Trim();
        return fallback.Length == 0 || string.Equals(fallback, fullCleanTitle, StringComparison.Ordinal)
            ? null
            : fallback;
    }

    private static List<string> DeduplicateTitles(IReadOnlyList<string> source)
    {
        List<string> titles = [];
        HashSet<string> seen = [];

        foreach (var item in source)
        {
            var trimmed = item?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var key = NormalizeTitle(trimmed);
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
            {
                continue;
            }

            titles.Add(trimmed);
        }

        return titles;
    }

    private static void AddIfPresent(List<string> titles, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            titles.Add(value.Trim());
        }
    }

    private static string NormalizeTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }
}
