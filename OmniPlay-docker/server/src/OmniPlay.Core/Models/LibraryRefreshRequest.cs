namespace OmniPlay.Core.Models;

public sealed record LibraryRefreshRequest(
    string SortKey = "year",
    string SortDirection = "desc");
