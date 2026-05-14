namespace OmniPlay.Core.Settings;

public sealed record LibraryViewSettings
{
    public const string SortOptionTitle = "title";

    public const string SortOptionYear = "year";

    public const string SortOptionRating = "rating";

    public string SortOption { get; init; } = SortOptionTitle;

    public bool SortDescending { get; init; }
}
