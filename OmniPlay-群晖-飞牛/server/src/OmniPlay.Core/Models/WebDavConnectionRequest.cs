namespace OmniPlay.Core.Models;

public sealed record WebDavConnectionRequest(
    string Url,
    string? Username = null,
    string? Password = null);
