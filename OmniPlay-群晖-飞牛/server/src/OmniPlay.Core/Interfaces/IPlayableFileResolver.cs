using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface IPlayableFileResolver
{
    Task<PlayableVideoFile?> ResolveAsync(
        string videoFileId,
        CancellationToken cancellationToken = default);
}
