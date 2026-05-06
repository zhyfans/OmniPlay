namespace OmniPlay.Core.Models;

public sealed record LocalDirectoryBrowseResult(
    string CurrentPath,
    string? ParentPath,
    IReadOnlyList<LocalDirectoryEntry> Entries);

public sealed record LocalDirectoryEntry(
    string Name,
    string Path,
    bool IsReadable,
    bool IsHidden);
