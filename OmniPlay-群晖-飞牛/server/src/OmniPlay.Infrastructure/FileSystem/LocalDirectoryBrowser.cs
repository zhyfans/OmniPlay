using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.FileSystem;

public sealed class LocalDirectoryBrowser : ILocalDirectoryBrowser
{
    private readonly IReadOnlyList<string>? configuredShareRoots;

    public LocalDirectoryBrowser()
    {
    }

    public LocalDirectoryBrowser(IReadOnlyList<string> configuredShareRoots)
    {
        this.configuredShareRoots = configuredShareRoots;
    }

    public Task<LocalDirectoryBrowseResult> BrowseAsync(
        string? path,
        CancellationToken cancellationToken = default)
    {
        if (ShouldShowSharedFolders(path))
        {
            return Task.FromResult(BrowseSharedFolders(cancellationToken));
        }

        var currentPath = ResolvePath(path);
        if (!Directory.Exists(currentPath))
        {
            throw new DirectoryNotFoundException($"目录不存在：{currentPath}");
        }

        var sharedRoots = ResolveSharedFolders(cancellationToken);
        var currentShareRoot = sharedRoots.Count > 0 ? FindContainingSharedRoot(currentPath, sharedRoots) : null;
        if (sharedRoots.Count > 0 && currentShareRoot is null)
        {
            if (IsVolumeRoot(currentPath))
            {
                return Task.FromResult(BrowseSharedFolders(cancellationToken));
            }

            throw new UnauthorizedAccessException("只能浏览已设置的共享文件夹。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var parentPath = currentShareRoot is null
            ? ResolveUnrestrictedParentPath(currentPath)
            : ResolveParentPath(currentPath, currentShareRoot);
        var entries = EnumerateDirectories(currentPath, cancellationToken)
            .Where(static entry => !IsSystemDirectoryName(entry.Name))
            .OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult(new LocalDirectoryBrowseResult(currentPath, parentPath, entries));
    }

    private static bool ShouldShowSharedFolders(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        var fullPath = ResolvePath(path);
        return Path.GetPathRoot(fullPath) is { } root
               && string.Equals(
                   Path.TrimEndingDirectorySeparator(fullPath),
                   Path.TrimEndingDirectorySeparator(root),
                   PathComparison);
    }

    private LocalDirectoryBrowseResult BrowseSharedFolders(CancellationToken cancellationToken)
    {
        var entries = ResolveSharedFolders(cancellationToken)
            .Select(path =>
            {
                var info = new DirectoryInfo(path);
                return new LocalDirectoryEntry(
                    info.Name,
                    info.FullName,
                    CanRead(info),
                    info.Attributes.HasFlag(FileAttributes.Hidden));
            })
            .OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new LocalDirectoryBrowseResult(Path.DirectorySeparatorChar.ToString(), null, entries);
    }

    private IReadOnlyList<string> ResolveSharedFolders(CancellationToken cancellationToken)
    {
        if (configuredShareRoots is { Count: > 0 })
        {
            return configuredShareRoots
                .Select(ResolvePath)
                .Where(Directory.Exists)
                .Where(path => !IsSystemDirectoryName(Path.GetFileName(path)))
                .Distinct(PathComparer)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var configuredRoots = Environment.GetEnvironmentVariable("OMNIPLAY_LOCAL_SHARE_ROOTS");
        if (!string.IsNullOrWhiteSpace(configuredRoots))
        {
            return configuredRoots
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ResolvePath)
                .Where(Directory.Exists)
                .Where(path => !IsSystemDirectoryName(Path.GetFileName(path)))
                .Distinct(PathComparer)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        if (OperatingSystem.IsWindows())
        {
            return [];
        }

        var volumeRoots = EnumerateVolumeRoots(cancellationToken);
        List<string> shares = [];
        foreach (var volumeRoot in volumeRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                shares.AddRange(Directory.EnumerateDirectories(volumeRoot)
                    .Where(path => !IsSystemDirectoryName(Path.GetFileName(path))));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Ignore inaccessible volumes; DSM shared folders on other volumes can still be listed.
            }
        }

        return shares
            .Distinct(PathComparer)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> EnumerateVolumeRoots(CancellationToken cancellationToken)
    {
        try
        {
            return Directory.EnumerateDirectories(Path.DirectorySeparatorChar.ToString(), "volume*")
                .Where(path =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(path);
                    return IsSynologyVolumeRootName(name);
                })
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static string ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return OperatingSystem.IsWindows()
                ? Path.GetPathRoot(Environment.SystemDirectory) ?? Directory.GetCurrentDirectory()
                : Path.DirectorySeparatorChar.ToString();
        }

        return Path.GetFullPath(path.Trim());
    }

    private static string? ResolveParentPath(string currentPath, string shareRoot)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentPath));
        var normalizedShareRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(shareRoot));
        if (string.Equals(normalized, normalizedShareRoot, PathComparison))
        {
            return Path.DirectorySeparatorChar.ToString();
        }

        return Directory.GetParent(normalized)?.FullName;
    }

    private static string? ResolveUnrestrictedParentPath(string currentPath)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentPath));
        var root = Path.GetPathRoot(normalized);
        if (string.Equals(normalized, Path.TrimEndingDirectorySeparator(root ?? string.Empty), PathComparison))
        {
            return null;
        }

        return Directory.GetParent(normalized)?.FullName;
    }

    private static string? FindContainingSharedRoot(string currentPath, IReadOnlyList<string> sharedRoots)
    {
        var normalizedCurrentPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentPath));
        return sharedRoots
            .Select(static path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Where(root => string.Equals(normalizedCurrentPath, root, PathComparison)
                           || normalizedCurrentPath.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
            .OrderByDescending(static path => path.Length)
            .FirstOrDefault();
    }

    private static bool IsVolumeRoot(string currentPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentPath));
        var parent = Directory.GetParent(normalized)?.FullName;
        return string.Equals(parent, Path.DirectorySeparatorChar.ToString(), PathComparison)
               && IsSynologyVolumeRootName(Path.GetFileName(normalized));
    }

    private static IEnumerable<LocalDirectoryEntry> EnumerateDirectories(
        string currentPath,
        CancellationToken cancellationToken)
    {
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(currentPath);
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }

        List<LocalDirectoryEntry> entries = [];
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new DirectoryInfo(directory);
                entries.Add(new LocalDirectoryEntry(
                    info.Name,
                    info.FullName,
                    CanRead(info),
                    info.Attributes.HasFlag(FileAttributes.Hidden)));
            }
            catch (UnauthorizedAccessException)
            {
                // The parent listed a directory that became inaccessible; skip it.
            }
            catch (IOException)
            {
                // The directory may have disappeared during enumeration.
            }
        }

        return entries;
    }

    private static bool CanRead(DirectoryInfo directory)
    {
        try
        {
            using var enumerator = directory.EnumerateFileSystemInfos().GetEnumerator();
            _ = enumerator.MoveNext();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool IsSystemDirectoryName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        return name.StartsWith('@')
               || name.StartsWith('#')
               || name.StartsWith('.')
               || string.Equals(name, "lost+found", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "aquota.group", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "aquota.user", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSynologyVolumeRootName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith("volume", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = name["volume".Length..];
        if (suffix.All(char.IsDigit))
        {
            return suffix.Length > 0;
        }

        if (suffix.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
        {
            return suffix["USB".Length..].All(char.IsDigit);
        }

        if (suffix.StartsWith("SATA", StringComparison.OrdinalIgnoreCase))
        {
            return suffix["SATA".Length..].All(char.IsDigit);
        }

        return false;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
