using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface IMediaSourceCleanupService
{
    Task<MediaSourceCleanupSummary> CleanupRemovedSourceAsync(
        long sourceId,
        IProgress<BackgroundTaskProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
