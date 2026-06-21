using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Core.Runtime;

public static class SubtitleCachePathResolver
{
    public static string ResolveRoot(IStoragePaths storagePaths, CacheSettings settings)
    {
        var configuredPath = settings.SubtitleCachePath.Trim();
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.Combine(storagePaths.CacheDirectory, "subtitles");
        }

        var expandedPath = Environment.ExpandEnvironmentVariables(configuredPath);
        return Path.GetFullPath(Path.IsPathRooted(expandedPath)
            ? expandedPath
            : Path.Combine(storagePaths.RootDirectory, expandedPath));
    }
}
