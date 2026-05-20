using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface IScanStatusStore
{
    LibraryScanStatus Get();

    void MarkStarted(DateTimeOffset startedAt);

    void MarkProgress(LibraryScanProgress progress);

    void MarkCancellationRequested(DateTimeOffset requestedAt);

    void MarkCompleted(LibraryScanSummary summary, DateTimeOffset completedAt);

    void MarkCanceled(DateTimeOffset canceledAt);

    void MarkFailed(string message, DateTimeOffset failedAt);
}
