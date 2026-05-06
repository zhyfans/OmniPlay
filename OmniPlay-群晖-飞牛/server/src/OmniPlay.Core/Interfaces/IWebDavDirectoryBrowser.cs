using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface IWebDavDirectoryBrowser
{
    Task<WebDavConnectionTestResult> TestConnectionAsync(
        string url,
        string? username,
        string? password,
        CancellationToken cancellationToken = default);

    Task<WebDavDirectoryBrowseResult> BrowseAsync(
        string url,
        string? username,
        string? password,
        CancellationToken cancellationToken = default);
}
