using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface ILocalDirectoryBrowser
{
    Task<LocalDirectoryBrowseResult> BrowseAsync(
        string? path,
        CancellationToken cancellationToken = default);
}
