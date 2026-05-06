namespace OmniPlay.Core.Models;

public sealed record RuntimeSelfCheckSnapshot(
    string Status,
    DateTimeOffset CheckedAt,
    IReadOnlyList<RuntimeSelfCheckItem> Items);

public sealed record RuntimeSelfCheckItem(
    string Key,
    string Label,
    string Status,
    string Detail,
    IReadOnlyDictionary<string, string>? Data = null);
