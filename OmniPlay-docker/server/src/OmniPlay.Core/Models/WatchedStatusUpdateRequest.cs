namespace OmniPlay.Core.Models;

public sealed record WatchedStatusUpdateRequest(
    string VideoFileId,
    bool IsWatched,
    double? DurationSeconds = null,
    string UserId = "local");

