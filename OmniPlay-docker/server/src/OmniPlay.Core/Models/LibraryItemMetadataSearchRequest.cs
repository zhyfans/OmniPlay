namespace OmniPlay.Core.Models;

public sealed record LibraryItemMetadataSearchRequest(
    string? Query,
    string? MediaType,
    string? Year);
