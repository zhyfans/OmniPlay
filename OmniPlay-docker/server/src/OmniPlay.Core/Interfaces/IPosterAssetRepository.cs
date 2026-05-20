using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface IPosterAssetRepository
{
    Task<PosterAsset?> GetAsync(string id, CancellationToken cancellationToken = default);
}

