using OmniPlay.Core.Models.Entities;

namespace OmniPlay.Infrastructure.Library;

internal static class LibraryPreferredTitleResolver
{
    public static string Resolve(
        string matchedTitle,
        string currentTitle,
        string? language,
        string? sourceProtocolType,
        string? baseUrl,
        string? relativePath,
        string? fileName = null)
    {
        if (string.IsNullOrWhiteSpace(matchedTitle))
        {
            return currentTitle;
        }

        if (ContainsHan(currentTitle) && !ContainsHan(matchedTitle))
        {
            return currentTitle;
        }

        if (IsEnglishLanguage(language) || ContainsHan(matchedTitle))
        {
            return matchedTitle;
        }

        var chineseFallback = ResolveChineseFallback(sourceProtocolType, baseUrl, relativePath, fileName);
        return !string.IsNullOrWhiteSpace(chineseFallback)
            ? chineseFallback
            : matchedTitle;
    }

    private static string? ResolveChineseFallback(
        string? sourceProtocolType,
        string? baseUrl,
        string? relativePath,
        string? fileName)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var fileNameChinese = MediaNameParser.ExtractSearchMetadata(fileName).ChineseTitle;
            if (!string.IsNullOrWhiteSpace(fileNameChinese))
            {
                return fileNameChinese;
            }
        }

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var metadataPath = MediaSourcePathResolver.ResolveMetadataPath(
            sourceProtocolType,
            baseUrl,
            relativePath);
        return MediaNameParser.ExtractParentFolderChineseTitle(metadataPath)
               ?? MediaNameParser.ExtractSearchMetadata(metadataPath).ChineseTitle;
    }

    private static bool IsEnglishLanguage(string? language)
    {
        return !string.IsNullOrWhiteSpace(language) &&
               language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsHan(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character >= 0x4E00 && character <= 0x9FFF)
            {
                return true;
            }
        }

        return false;
    }
}
