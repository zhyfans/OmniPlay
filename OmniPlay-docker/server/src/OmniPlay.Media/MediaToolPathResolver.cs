namespace OmniPlay.Media;

internal static class MediaToolPathResolver
{
    public static string ResolveFfmpegPath()
    {
        return ResolveToolPath("OMNIPLAY_FFMPEG_PATH", "ffmpeg");
    }

    public static string ResolveFfprobePath()
    {
        return ResolveToolPath("OMNIPLAY_FFPROBE_PATH", "ffprobe");
    }

    private static string ResolveToolPath(string environmentVariable, string executableName)
    {
        var configuredPath = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        foreach (var candidate in CandidatePaths(executableName))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return executableName;
    }

    private static IEnumerable<string> CandidatePaths(string executableName)
    {
        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, "bin", executableName);
        yield return Path.Combine(baseDirectory, "tools", "ffmpeg", "bin", executableName);
        yield return Path.Combine(baseDirectory, executableName);

        foreach (var packageName in new[]
                 {
                     "ffmpeg7",
                     "ffmpeg6",
                     "ffmpeg5",
                     "ffmpeg4",
                     "ffmpeg",
                     "jellyfin-ffmpeg",
                     "Jellyfin"
                 })
        {
            yield return Path.Combine("/var/packages", packageName, "target", "bin", executableName);
        }

        foreach (var synologyPackagePath in SynologyPackageToolPaths(executableName))
        {
            yield return synologyPackagePath;
        }

        yield return Path.Combine("/usr/local/bin", executableName);
        yield return Path.Combine("/usr/syno/bin", executableName);
        yield return Path.Combine("/usr/bin", executableName);
        yield return Path.Combine("/bin", executableName);
    }

    private static IEnumerable<string> SynologyPackageToolPaths(string executableName)
    {
        foreach (var packageName in new[]
                 {
                     "CodecPack",
                     "VideoStation",
                     "MediaServer",
                     "AdvancedMediaExtensions"
                 })
        {
            yield return Path.Combine("/var/packages", packageName, "target", "bin", executableName);
        }

        if (executableName == "ffmpeg")
        {
            yield return "/var/packages/CodecPack/target/bin/ffmpeg41";
            yield return "/var/packages/VideoStation/target/bin/ffmpeg41";
            yield return "/usr/syno/bin/synoffmpeg";
        }
        else if (executableName == "ffprobe")
        {
            yield return "/var/packages/CodecPack/target/bin/ffprobe41";
            yield return "/var/packages/VideoStation/target/bin/ffprobe41";
        }
    }
}
