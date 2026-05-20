namespace OmniPlay.Core.Models;

public sealed record LibraryMetadataEnrichmentProgress(
    string Phase,
    int TargetItemCount,
    int ProcessedItemCount,
    int MatchedItemCount,
    int UpdatedItemCount,
    int DownloadedPosterCount,
    string? CurrentItemId,
    string? CurrentTitle,
    DateTimeOffset UpdatedAt,
    int? PhaseTargetCount = null,
    int? PhaseProcessedCount = null);
