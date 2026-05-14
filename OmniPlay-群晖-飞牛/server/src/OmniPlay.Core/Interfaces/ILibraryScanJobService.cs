using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface ILibraryScanJobService
{
    bool TryStartScan(LibraryRefreshRequest order, out LibraryScanStatus status);

    bool TryStartSourceScan(long sourceId, LibraryRefreshRequest order, out LibraryScanStatus status);

    bool RequestCancel(out LibraryScanStatus status);
}
