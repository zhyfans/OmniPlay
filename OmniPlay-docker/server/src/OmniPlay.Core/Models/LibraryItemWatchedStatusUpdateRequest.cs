namespace OmniPlay.Core.Models;

public sealed record LibraryItemWatchedStatusUpdateRequest(
    string LibraryItemId,
    bool IsWatched,
    string UserId = "local");
