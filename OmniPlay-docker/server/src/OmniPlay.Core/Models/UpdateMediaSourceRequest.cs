namespace OmniPlay.Core.Models;

public sealed record UpdateMediaSourceRequest(
    string? Name = null,
    bool? IsEnabled = null);
