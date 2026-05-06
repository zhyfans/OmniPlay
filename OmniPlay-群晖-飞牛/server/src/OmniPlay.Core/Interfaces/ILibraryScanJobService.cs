using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface ILibraryScanJobService
{
    bool TryStartScan(out LibraryScanStatus status);

    bool TryStartSourceScan(long sourceId, out LibraryScanStatus status);

    bool RequestCancel(out LibraryScanStatus status);
}
