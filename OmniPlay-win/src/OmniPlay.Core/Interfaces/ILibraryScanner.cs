using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface ILibraryScanner
{
    Task<LibraryScanSummary> ScanAllAsync(CancellationToken cancellationToken = default);

    Task<LibraryScanSummary> ScanSourceAsync(
        long sourceId,
        CancellationToken cancellationToken = default,
        Func<LibraryScanIndexedItem, CancellationToken, Task>? afterItemIndexed = null,
        bool deferUnidentifiedGroups = false);

    void ClearDeferredUnidentifiedScanGroups();

    Task<LibraryScanSummary> CommitDeferredUnidentifiedScanGroupsAsync(CancellationToken cancellationToken = default);
}
