using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface IThumbnailAssetRepository
{
    Task<ThumbnailAsset?> GetAsync(string id, CancellationToken cancellationToken = default);
}
