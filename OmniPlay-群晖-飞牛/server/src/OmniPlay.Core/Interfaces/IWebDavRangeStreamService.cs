using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface IWebDavRangeStreamService
{
    Task<PlayableVideoFile?> GetFileInfoAsync(
        string videoFileId,
        CancellationToken cancellationToken = default);

    Task<WebDavRangeStreamResult?> OpenReadAsync(
        string videoFileId,
        string? rangeHeader,
        CancellationToken cancellationToken = default);
}
