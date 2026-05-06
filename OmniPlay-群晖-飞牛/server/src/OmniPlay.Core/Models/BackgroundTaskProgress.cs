namespace OmniPlay.Core.Models;

public sealed record BackgroundTaskProgress(
    string Phase,
    string? Message,
    double? Percent,
    string? CurrentItem);
