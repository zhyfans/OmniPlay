namespace OmniPlay.Core.Models;

public sealed record LibraryScanStatus(
    bool IsRunning,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    LibraryScanSummary? LastSummary,
    string? LastError,
    bool IsCancellationRequested = false,
    DateTimeOffset? CancellationRequestedAt = null,
    LibraryScanProgress? Progress = null,
    bool WasCanceled = false);
