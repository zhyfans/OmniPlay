namespace OmniPlay.Core.Models;

public sealed record LibraryScanSummary(
    int SourceCount,
    int NewMovieCount,
    int NewVideoFileCount,
    int RemovedVideoFileCount = 0,
    int NewTvShowCount = 0,
    IReadOnlyList<string>? Diagnostics = null)
{
    public IReadOnlyList<string> Diagnostics { get; init; } = Diagnostics ?? [];

    public bool HasDiagnostics => Diagnostics.Count > 0;
}

