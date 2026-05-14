namespace OmniPlay.Core.Models;

public sealed record TmdbConnectionTestResult(
    bool IsReachable,
    string Source,
    int? StatusCode,
    string Message);
