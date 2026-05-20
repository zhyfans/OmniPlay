using OmniPlay.Infrastructure.FileSystem;
using Xunit;

namespace OmniPlay.Tests;

public sealed class StoragePathsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omniplay-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void EnsureCreatedCreatesRuntimeDirectories()
    {
        var paths = new StoragePaths(root);

        paths.EnsureCreated();

        Assert.True(Directory.Exists(paths.DataDirectory));
        Assert.True(Directory.Exists(paths.CacheDirectory));
        Assert.True(Directory.Exists(paths.SettingsDirectory));
        Assert.True(Directory.Exists(paths.PostersDirectory));
        Assert.True(Directory.Exists(paths.ThumbnailsDirectory));
        Assert.True(Directory.Exists(paths.TranscodeDirectory));
        Assert.True(Directory.Exists(paths.LogsDirectory));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
