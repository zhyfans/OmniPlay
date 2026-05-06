namespace OmniPlay.Core.Models;

public sealed record WebDavDirectoryBrowseResult(
    string CurrentUrl,
    string? ParentUrl,
    IReadOnlyList<WebDavDirectoryEntry> Entries);

public sealed record WebDavDirectoryEntry(
    string Name,
    string Url,
    bool IsReadable,
    DateTimeOffset? LastModified);
