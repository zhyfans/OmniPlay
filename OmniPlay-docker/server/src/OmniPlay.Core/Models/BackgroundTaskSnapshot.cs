namespace OmniPlay.Core.Models;

public sealed record BackgroundTaskSnapshot(
    IReadOnlyList<BackgroundTaskStatus> Tasks,
    BackgroundTaskStatus? ActiveTask);
