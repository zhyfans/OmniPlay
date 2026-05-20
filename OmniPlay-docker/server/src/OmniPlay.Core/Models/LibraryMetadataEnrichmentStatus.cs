namespace OmniPlay.Core.Models;

public sealed record LibraryMetadataEnrichmentStatus(
    bool IsRunning,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    LibraryMetadataEnrichmentSummary? LastSummary,
    string? LastError,
    bool IsCancellationRequested = false,
    DateTimeOffset? CancellationRequestedAt = null,
    LibraryMetadataEnrichmentProgress? Progress = null,
    bool WasCanceled = false,
    string? TargetLibraryItemId = null);
