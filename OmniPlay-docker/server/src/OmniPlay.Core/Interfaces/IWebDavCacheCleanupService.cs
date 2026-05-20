using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface IWebDavCacheCleanupService
{
    Task<WebDavCacheCleanupSummary> CleanupAsync(
        TimeSpan maxAge,
        IProgress<BackgroundTaskProgress>? progress = null,
        CancellationToken cancellationToken = default,
        long? maxBytes = null);
}
