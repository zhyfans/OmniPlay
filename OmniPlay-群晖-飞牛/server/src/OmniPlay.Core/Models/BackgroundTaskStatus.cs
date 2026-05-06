namespace OmniPlay.Core.Models;

public sealed record BackgroundTaskStatus(
    string Id,
    string Kind,
    string Title,
    string State,
    bool IsRunning,
    bool IsCancellationRequested,
    bool CanCancel,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Phase,
    string? ProgressText,
    double? ProgressPercent,
    string? CurrentItem,
    string? ResultText,
    string? ErrorMessage);
