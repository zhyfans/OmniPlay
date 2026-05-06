namespace OmniPlay.Core.Models;

public sealed record VideoFileStreamSummary(
    int Index,
    string Kind,
    string? Codec,
    string? Language,
    string? Title,
    int? Channels,
    string? ChannelLayout,
    bool IsDefault,
    bool IsForced);
