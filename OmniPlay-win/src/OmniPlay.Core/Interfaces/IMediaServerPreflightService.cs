using OmniPlay.Core.Models;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Core.Models.Network;

namespace OmniPlay.Core.Interfaces;

public interface IMediaServerPreflightService
{
    Task<MediaServerPreflightResult> PreScanAsync(
        MediaSource source,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NetworkShareFolderItem>> ListLibrariesAsync(
        MediaSource source,
        CancellationToken cancellationToken = default);
}
