using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface ILibraryMetadataEnricher
{
    Task<LibraryMetadataEnrichmentSummary> EnrichMissingAsync(CancellationToken cancellationToken = default);

    Task<LibraryMetadataEnrichmentSummary> EnrichMissingAsync(
        IProgress<LibraryMetadataEnrichmentProgress>? progress,
        CancellationToken cancellationToken = default);

    Task<LibraryMetadataEnrichmentSummary> EnrichMissingAsync(
        IProgress<LibraryMetadataEnrichmentProgress>? progress,
        LibraryRefreshRequest order,
        CancellationToken cancellationToken = default);

    Task<LibraryMetadataEnrichmentSummary> EnrichItemAsync(
        string libraryItemId,
        CancellationToken cancellationToken = default);

    Task<LibraryMetadataEnrichmentSummary> EnrichItemAsync(
        string libraryItemId,
        IProgress<LibraryMetadataEnrichmentProgress>? progress,
        CancellationToken cancellationToken = default);
}
