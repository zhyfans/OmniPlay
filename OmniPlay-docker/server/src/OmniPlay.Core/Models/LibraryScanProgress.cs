namespace OmniPlay.Core.Models;

public sealed record LibraryScanProgress(
    string Phase,
    int SourceCount,
    int CompletedSourceCount,
    string? CurrentSourceName,
    int TotalVideoFileCount,
    int ProcessedVideoFileCount,
    int ProbeCandidateCount,
    int ProbedVideoFileCount,
    string? CurrentRelativePath,
    DateTimeOffset UpdatedAt);
