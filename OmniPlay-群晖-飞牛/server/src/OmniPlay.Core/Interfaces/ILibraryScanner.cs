using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface ILibraryScanner
{
    Task<LibraryScanSummary> ScanAllAsync(CancellationToken cancellationToken = default);

    Task<LibraryScanSummary> ScanAllAsync(
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken = default);

    Task<LibraryScanSummary> ScanSourceAsync(
        long sourceId,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken = default);
}
