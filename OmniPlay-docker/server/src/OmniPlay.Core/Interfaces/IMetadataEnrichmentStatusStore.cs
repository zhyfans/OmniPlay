using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface IMetadataEnrichmentStatusStore
{
    LibraryMetadataEnrichmentStatus Get();

    void MarkStarted(string? targetLibraryItemId, DateTimeOffset startedAt);

    void MarkProgress(LibraryMetadataEnrichmentProgress progress);

    void MarkCancellationRequested(DateTimeOffset requestedAt);

    void MarkCompleted(LibraryMetadataEnrichmentSummary summary, DateTimeOffset completedAt);

    void MarkCanceled(DateTimeOffset canceledAt);

    void MarkFailed(string message, DateTimeOffset failedAt);
}
