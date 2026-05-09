namespace OmniPlay.Core.Models;

public sealed record LibraryScanIndexedItem(
    long Id,
    string Title,
    string MediaType,
    int VideoFileCount)
{
    public bool IsMovie => string.Equals(MediaType, "movie", StringComparison.OrdinalIgnoreCase);

    public bool IsTvShow => string.Equals(MediaType, "tv", StringComparison.OrdinalIgnoreCase);
}
