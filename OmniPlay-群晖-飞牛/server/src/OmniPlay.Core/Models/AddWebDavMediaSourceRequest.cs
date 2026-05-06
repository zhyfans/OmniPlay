namespace OmniPlay.Core.Models;

public sealed record AddWebDavMediaSourceRequest(
    string Url,
    string? Name = null,
    string? Username = null,
    string? Password = null);
