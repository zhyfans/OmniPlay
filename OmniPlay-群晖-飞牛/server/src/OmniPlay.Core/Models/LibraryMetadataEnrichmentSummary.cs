namespace OmniPlay.Core.Models;

public sealed record LibraryMetadataEnrichmentSummary(
    int ScannedItems = 0,
    int MatchedItems = 0,
    int UpdatedItems = 0,
    int DownloadedPosters = 0,
    IReadOnlyList<string>? Diagnostics = null)
{
    public IReadOnlyList<string> Diagnostics { get; init; } = Diagnostics ?? [];
}

