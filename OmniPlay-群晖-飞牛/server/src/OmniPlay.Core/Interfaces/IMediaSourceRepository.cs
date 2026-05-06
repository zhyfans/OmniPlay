using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface IMediaSourceRepository
{
    Task<IReadOnlyList<MediaSourceSummary>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<MediaSourceSummary> AddLocalAsync(
        string name,
        string path,
        CancellationToken cancellationToken = default);

    Task<MediaSourceSummary> AddWebDavAsync(
        string name,
        string url,
        string? username,
        string? password,
        CancellationToken cancellationToken = default);

    Task<MediaSourceSummary?> UpdateAsync(
        long id,
        UpdateMediaSourceRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(
        long id,
        CancellationToken cancellationToken = default);
}
