using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface ILibraryMetadataEnrichmentJobService
{
    bool TryStartMissing(out LibraryMetadataEnrichmentStatus status);

    bool TryStartItem(string libraryItemId, out LibraryMetadataEnrichmentStatus status);

    bool RequestCancel(out LibraryMetadataEnrichmentStatus status);
}
