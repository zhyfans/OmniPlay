using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Core.Runtime;

public static class HlsCachePathResolver
{
    public static string ResolveRoot(IStoragePaths storagePaths, CacheSettings settings)
    {
        var configuredPath = settings.HlsCachePath.Trim();
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return storagePaths.TranscodeDirectory;
        }

        var expandedPath = Environment.ExpandEnvironmentVariables(configuredPath);
        return Path.GetFullPath(Path.IsPathRooted(expandedPath)
            ? expandedPath
            : Path.Combine(storagePaths.RootDirectory, expandedPath));
    }
}
