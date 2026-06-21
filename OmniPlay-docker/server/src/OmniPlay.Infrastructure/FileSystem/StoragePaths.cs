using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Runtime;

namespace OmniPlay.Infrastructure.FileSystem;

public sealed class StoragePaths : IStoragePaths
{
    public StoragePaths()
        : this(null)
    {
    }

    public StoragePaths(string? rootDirectory, string? cacheDirectory = null)
    {
        RootDirectory = ResolveRootDirectory(rootDirectory);
        DataDirectory = Path.Combine(RootDirectory, "data");
        CacheDirectory = ResolveCacheDirectory(RootDirectory, cacheDirectory);
        SettingsDirectory = Path.Combine(RootDirectory, "settings");
        PostersDirectory = Path.Combine(CacheDirectory, "posters");
        ThumbnailsDirectory = Path.Combine(CacheDirectory, "thumbnails");
        TranscodeDirectory = Path.Combine(CacheDirectory, "transcode");
        LogsDirectory = Path.Combine(RootDirectory, "logs");
    }

    public string RootDirectory { get; }
    public string DataDirectory { get; }
    public string CacheDirectory { get; }
    public string SettingsDirectory { get; }
    public string PostersDirectory { get; }
    public string ThumbnailsDirectory { get; }
    public string TranscodeDirectory { get; }
    public string LogsDirectory { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(SettingsDirectory);
        Directory.CreateDirectory(PostersDirectory);
        Directory.CreateDirectory(ThumbnailsDirectory);
        Directory.CreateDirectory(TranscodeDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    private static string ResolveRootDirectory(string? rootDirectory)
    {
        var explicitRoot = rootDirectory;
        if (string.IsNullOrWhiteSpace(explicitRoot))
        {
            explicitRoot = Environment.GetEnvironmentVariable(AppRuntime.AppRootEnvironmentVariable);
        }

        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(explicitRoot));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            home = AppContext.BaseDirectory;
        }

        return Path.Combine(home, ".local", "share", "OmniPlay");
    }

    private static string ResolveCacheDirectory(string rootDirectory, string? cacheDirectory)
    {
        var explicitCache = cacheDirectory;
        if (string.IsNullOrWhiteSpace(explicitCache))
        {
            explicitCache = Environment.GetEnvironmentVariable(AppRuntime.CacheRootEnvironmentVariable);
        }

        if (string.IsNullOrWhiteSpace(explicitCache))
        {
            return Path.Combine(rootDirectory, "cache");
        }

        var expanded = Environment.ExpandEnvironmentVariables(explicitCache);
        return Path.GetFullPath(Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(rootDirectory, expanded));
    }
}
