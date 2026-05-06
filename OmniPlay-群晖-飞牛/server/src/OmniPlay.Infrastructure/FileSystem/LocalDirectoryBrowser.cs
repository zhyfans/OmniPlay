using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.FileSystem;

public sealed class LocalDirectoryBrowser : ILocalDirectoryBrowser
{
    public Task<LocalDirectoryBrowseResult> BrowseAsync(
        string? path,
        CancellationToken cancellationToken = default)
    {
        var currentPath = ResolvePath(path);
        if (!Directory.Exists(currentPath))
        {
            throw new DirectoryNotFoundException($"目录不存在：{currentPath}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var parentPath = ResolveParentPath(currentPath);
        var entries = EnumerateDirectories(currentPath, cancellationToken)
            .OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult(new LocalDirectoryBrowseResult(currentPath, parentPath, entries));
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

    private static string? ResolveParentPath(string currentPath)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentPath));
        var root = Path.GetPathRoot(normalized);
        if (string.Equals(normalized, Path.TrimEndingDirectorySeparator(root ?? string.Empty), PathComparison))
        {
            return null;
        }

        return Directory.GetParent(normalized)?.FullName;
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

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
