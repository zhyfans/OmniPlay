namespace OmniPlay.Core.Models;

public sealed record LibraryItemLockUpdateRequest(
    string LibraryItemId,
    bool IsLocked);
