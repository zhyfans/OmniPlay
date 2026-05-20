namespace OmniPlay.Core.Models;

public sealed record ProxySettings(
    bool IsEnabled = false,
    string Url = "",
    string Username = "",
    string Password = "",
    string BypassList = "");

public sealed record ProxyConnectionTestResult(
    bool IsReachable,
    string ProxyUrl,
    string TargetUrl,
    int? StatusCode,
    string Message);
