namespace OmniPlay.Core.Models;

public sealed record MediaServerPreflightResult(
    bool Success,
    string Message,
    int MediaFileCount = 0,
    IReadOnlyList<string>? SampleFileNames = null);
