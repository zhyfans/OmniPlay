namespace OmniPlay.Core.Models;

public sealed record WebDavConnectionTestResult(
    bool IsReachable,
    string Url,
    int? StatusCode,
    string Message);
