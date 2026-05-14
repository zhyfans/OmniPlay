using OmniPlay.Core.Models.Entities;

namespace OmniPlay.Tests;

public sealed class MediaSourcePathResolverTests
{
    [Fact]
    public void ResolveAuthenticatedPlaybackPath_AddsPlexDownloadAndTokenParameters()
    {
        var authConfig = MediaSourceAuthConfigSerializer.SerializeMediaServer(new MediaServerAuthConfig("token value"));

        var path = MediaSourcePathResolver.ResolveAuthenticatedPlaybackPath(
            MediaSourceProtocol.Plex,
            "http://127.0.0.1:32400",
            "/library/parts/15/1761294834/file.mkv",
            authConfig);

        Assert.Equal(
            "http://127.0.0.1:32400/library/parts/15/1761294834/file.mkv?download=1&X-Plex-Token=token%20value",
            path);
    }

    [Fact]
    public void ResolveAuthenticatedPlaybackPath_PreservesJellyfinHlsQueryAndAddsApiKey()
    {
        var authConfig = MediaSourceAuthConfigSerializer.SerializeMediaServer(new MediaServerAuthConfig("api value"));

        var path = MediaSourcePathResolver.ResolveAuthenticatedPlaybackPath(
            MediaSourceProtocol.Jellyfin,
            "http://192.168.0.150:8096",
            "Videos/218fe7342f645e3cf86f563115495a67/master.m3u8?mediaSourceId=218fe7342f645e3cf86f563115495a67&playSessionId=omniplay218fe7342f645e3cf86f563115495a67",
            authConfig);

        Assert.Equal(
            "http://192.168.0.150:8096/Videos/218fe7342f645e3cf86f563115495a67/master.m3u8?mediaSourceId=218fe7342f645e3cf86f563115495a67&playSessionId=omniplay218fe7342f645e3cf86f563115495a67&api_key=api%20value",
            path);
    }

    [Fact]
    public void ResolveLocalBluRayRoot_FromStreamFile_ReturnsDiscRoot()
    {
        var discRoot = CreateBluRayDiscLayout();
        try
        {
            var streamFile = Path.Combine(discRoot, "BDMV", "STREAM", "00001.m2ts");

            var resolvedRoot = MediaSourcePathResolver.ResolveLocalBluRayRoot(streamFile);

            Assert.Equal(Path.GetFullPath(discRoot), resolvedRoot);
        }
        finally
        {
            Directory.Delete(discRoot, recursive: true);
        }
    }

    [Fact]
    public void IsPlayableLocation_AcceptsBluRayDiscRootDirectory()
    {
        var discRoot = CreateBluRayDiscLayout();
        try
        {
            Assert.True(MediaSourcePathResolver.IsPlayableLocation(discRoot));
        }
        finally
        {
            Directory.Delete(discRoot, recursive: true);
        }
    }

    [Fact]
    public void ResolveLocalBluRayRoot_AcceptsSingleWrappedDiscRootDirectory()
    {
        var wrapperRoot = Path.Combine(Path.GetTempPath(), $"omniplay-bdmv-wrapper-{Guid.NewGuid():N}");
        var discRoot = Path.Combine(wrapperRoot, "DAT-CMCT");
        try
        {
            CreateBluRayDiscLayout(discRoot);

            var resolvedRoot = MediaSourcePathResolver.ResolveLocalBluRayRoot(wrapperRoot);

            Assert.Equal(Path.GetFullPath(discRoot), resolvedRoot);
            Assert.True(MediaSourcePathResolver.IsPlayableLocation(wrapperRoot));
        }
        finally
        {
            if (Directory.Exists(wrapperRoot))
            {
                Directory.Delete(wrapperRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void IsPlayableLocation_AcceptsDirectoryWithSingleIsoFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"omniplay-iso-folder-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            var isoPath = Path.Combine(directory, "Movie.iso");
            File.WriteAllText(isoPath, string.Empty);

            Assert.True(MediaSourcePathResolver.IsPlayableLocation(directory));
            Assert.Equal(isoPath, MediaSourcePathResolver.ResolvePlayableLocation(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void IsPlayableLocation_RejectsDirectoryWithMultipleIsoFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"omniplay-iso-folder-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "Movie-A.iso"), string.Empty);
            File.WriteAllText(Path.Combine(directory, "Movie-B.iso"), string.Empty);

            Assert.False(MediaSourcePathResolver.IsPlayableLocation(directory));
            Assert.Equal(directory, MediaSourcePathResolver.ResolvePlayableLocation(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveLocalDvdRoot_FromVideoTsDirectory_ReturnsDiscRoot()
    {
        var discRoot = CreateDvdDiscLayout();
        try
        {
            var resolvedRoot = MediaSourcePathResolver.ResolveLocalDvdRoot(
                Path.Combine(discRoot, "VIDEO_TS"));

            Assert.Equal(Path.GetFullPath(discRoot), resolvedRoot);
        }
        finally
        {
            Directory.Delete(discRoot, recursive: true);
        }
    }

    [Fact]
    public void ResolveLocalDvdRoot_FromVideoTsFile_ReturnsDiscRoot()
    {
        var discRoot = CreateDvdDiscLayout();
        try
        {
            var resolvedRoot = MediaSourcePathResolver.ResolveLocalDvdRoot(
                Path.Combine(discRoot, "VIDEO_TS", "VTS_01_1.VOB"));

            Assert.Equal(Path.GetFullPath(discRoot), resolvedRoot);
        }
        finally
        {
            Directory.Delete(discRoot, recursive: true);
        }
    }

    [Fact]
    public void IsPlayableLocation_AcceptsDvdDiscRootDirectory()
    {
        var discRoot = CreateDvdDiscLayout();
        try
        {
            Assert.True(MediaSourcePathResolver.IsPlayableLocation(discRoot));
        }
        finally
        {
            Directory.Delete(discRoot, recursive: true);
        }
    }

    [Fact]
    public void ResolveLocalBluRayMainFeaturePaths_UsesMacMainFeatureClusterRules()
    {
        var discRoot = CreateBluRayDiscLayout();
        try
        {
            CreateSizedFile(Path.Combine(discRoot, "BDMV", "STREAM", "00000.m2ts"), 10_000);
            CreateSizedFile(Path.Combine(discRoot, "BDMV", "STREAM", "00001.m2ts"), 9_200);
            CreateSizedFile(Path.Combine(discRoot, "BDMV", "STREAM", "00020.m2ts"), 1_000);

            var selected = MediaSourcePathResolver.ResolveLocalBluRayMainFeaturePaths(discRoot)
                .Select(Path.GetFileName)
                .ToList();

            Assert.Equal(["00000.m2ts", "00001.m2ts"], selected);
        }
        finally
        {
            Directory.Delete(discRoot, recursive: true);
        }
    }

    [Fact]
    public void ResolveLocalBluRayMainFeaturePaths_DropsSeparatedExtras()
    {
        var discRoot = CreateBluRayDiscLayout();
        try
        {
            CreateSizedFile(Path.Combine(discRoot, "BDMV", "STREAM", "00000.m2ts"), 10_000);
            CreateSizedFile(Path.Combine(discRoot, "BDMV", "STREAM", "00001.m2ts"), 4_000);
            CreateSizedFile(Path.Combine(discRoot, "BDMV", "STREAM", "00020.m2ts"), 1_000);

            var selected = MediaSourcePathResolver.ResolveLocalBluRayMainFeaturePaths(
                    Path.Combine(discRoot, "BDMV", "STREAM", "00001.m2ts"))
                .Select(Path.GetFileName)
                .ToList();

            Assert.Equal(["00000.m2ts"], selected);
        }
        finally
        {
            Directory.Delete(discRoot, recursive: true);
        }
    }

    [Fact]
    public void ResolveLocalBluRayMainFeatureSegments_UsesPlaylistDurations()
    {
        var discRoot = CreateBluRayDiscLayout();
        try
        {
            var playlistRoot = Path.Combine(discRoot, "BDMV", "PLAYLIST");
            Directory.CreateDirectory(playlistRoot);
            CreateSizedFile(Path.Combine(discRoot, "BDMV", "STREAM", "00010.m2ts"), 10_000);
            CreateSizedFile(Path.Combine(discRoot, "BDMV", "STREAM", "00011.m2ts"), 9_000);
            CreateMplsPlaylist(
                Path.Combine(playlistRoot, "00001.mpls"),
                [
                    ("00010", 0u, 45_000u),
                    ("00011", 45_000u, 135_000u)
                ]);

            var segments = MediaSourcePathResolver.ResolveLocalBluRayMainFeatureSegments(discRoot);

            Assert.Equal(["00010.m2ts", "00011.m2ts"], segments.Select(segment => Path.GetFileName(segment.Path)).ToList());
            Assert.Equal([1d, 2d], segments.Select(segment => segment.DurationSeconds).ToList());
        }
        finally
        {
            Directory.Delete(discRoot, recursive: true);
        }
    }

    private static string CreateBluRayDiscLayout()
    {
        var discRoot = Path.Combine(Path.GetTempPath(), $"omniplay-bdmv-{Guid.NewGuid():N}");
        return CreateBluRayDiscLayout(discRoot);
    }

    private static string CreateBluRayDiscLayout(string discRoot)
    {
        var bdmvRoot = Path.Combine(discRoot, "BDMV");
        var streamRoot = Path.Combine(bdmvRoot, "STREAM");
        Directory.CreateDirectory(streamRoot);
        File.WriteAllText(Path.Combine(bdmvRoot, "index.bdmv"), string.Empty);
        File.WriteAllText(Path.Combine(streamRoot, "00001.m2ts"), string.Empty);
        return discRoot;
    }

    private static string CreateDvdDiscLayout()
    {
        var discRoot = Path.Combine(Path.GetTempPath(), $"omniplay-dvd-{Guid.NewGuid():N}");
        var videoTsRoot = Path.Combine(discRoot, "VIDEO_TS");
        Directory.CreateDirectory(videoTsRoot);
        File.WriteAllText(Path.Combine(videoTsRoot, "VIDEO_TS.IFO"), string.Empty);
        File.WriteAllText(Path.Combine(videoTsRoot, "VTS_01_1.VOB"), string.Empty);
        return discRoot;
    }

    private static void CreateSizedFile(string path, long size)
    {
        using var stream = File.Open(path, FileMode.Create, FileAccess.Write);
        stream.SetLength(size);
    }

    private static void CreateMplsPlaylist(string path, IReadOnlyList<(string ClipName, uint InTime, uint OutTime)> items)
    {
        const int playlistStart = 32;
        const int itemLength = 20;
        var length = playlistStart + 10 + items.Count * (2 + itemLength);
        var bytes = new byte[length];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'P';
        bytes[2] = (byte)'L';
        bytes[3] = (byte)'S';
        WriteBigEndianUInt32(bytes, 8, playlistStart);
        WriteBigEndianUInt32(bytes, playlistStart, (uint)(6 + items.Count * (2 + itemLength)));
        WriteBigEndianUInt16(bytes, playlistStart + 6, (ushort)items.Count);

        var offset = playlistStart + 10;
        foreach (var item in items)
        {
            WriteBigEndianUInt16(bytes, offset, itemLength);
            var clipNameBytes = System.Text.Encoding.ASCII.GetBytes(item.ClipName);
            Array.Copy(clipNameBytes, 0, bytes, offset + 2, Math.Min(5, clipNameBytes.Length));
            bytes[offset + 7] = (byte)'M';
            bytes[offset + 8] = (byte)'2';
            bytes[offset + 9] = (byte)'T';
            bytes[offset + 10] = (byte)'S';
            WriteBigEndianUInt32(bytes, offset + 14, item.InTime);
            WriteBigEndianUInt32(bytes, offset + 18, item.OutTime);
            offset += 2 + itemLength;
        }

        File.WriteAllBytes(path, bytes);
    }

    private static void WriteBigEndianUInt16(byte[] bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)(value >> 8);
        bytes[offset + 1] = (byte)value;
    }

    private static void WriteBigEndianUInt32(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
    }
}
