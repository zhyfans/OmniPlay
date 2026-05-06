namespace OmniPlay.Core.Models;

public sealed record WebDavFileEntry(
    string Name,
    string Url,
    string RelativePath,
    long? ContentLength,
    DateTimeOffset? LastModified);
