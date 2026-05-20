using OmniPlay.Infrastructure.FileSystem;
using Xunit;

namespace OmniPlay.Tests;

public sealed class LocalDirectoryBrowserTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omniplay-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BrowseListsOnlyDirectoriesWithParent()
    {
        var movies = Directory.CreateDirectory(Path.Combine(root, "Movies")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "TV"));
        Directory.CreateDirectory(Path.Combine(root, ".hidden"));
        await File.WriteAllTextAsync(Path.Combine(root, "video.mp4"), "");

        var result = await new LocalDirectoryBrowser().BrowseAsync(root);

        Assert.Equal(Path.GetFullPath(root), result.CurrentPath);
        Assert.Equal(Directory.GetParent(root)?.FullName, result.ParentPath);
        Assert.Contains(result.Entries, entry => entry.Path == movies && entry.Name == "Movies" && entry.IsReadable);
        Assert.DoesNotContain(result.Entries, entry => entry.Name == ".hidden");
        Assert.DoesNotContain(result.Entries, entry => entry.Name == "video.mp4");
    }

    [Fact]
    public async Task BrowseRejectsMissingDirectory()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            new LocalDirectoryBrowser().BrowseAsync(Path.Combine(root, "missing")));
    }

    [Fact]
    public async Task BrowseDefaultListsConfiguredSharedFoldersOnly()
    {
        var movies = Directory.CreateDirectory(Path.Combine(root, "volume1", "Movies")).FullName;
        var tv = Directory.CreateDirectory(Path.Combine(root, "volume1", "TV")).FullName;
        var system = Directory.CreateDirectory(Path.Combine(root, "volume1", "@appstore")).FullName;

        var result = await new LocalDirectoryBrowser([movies, tv, system]).BrowseAsync(null);

        Assert.Equal(Path.DirectorySeparatorChar.ToString(), result.CurrentPath);
        Assert.Null(result.ParentPath);
        Assert.Contains(result.Entries, entry => entry.Path == movies && entry.Name == "Movies");
        Assert.Contains(result.Entries, entry => entry.Path == tv && entry.Name == "TV");
        Assert.DoesNotContain(result.Entries, entry => entry.Name == "@appstore");
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
