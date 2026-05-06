using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface IWebDavFileEnumerator
{
    Task<IReadOnlyList<WebDavFileEntry>> EnumerateFilesAsync(
        string rootUrl,
        string? username,
        string? password,
        CancellationToken cancellationToken = default);
}
