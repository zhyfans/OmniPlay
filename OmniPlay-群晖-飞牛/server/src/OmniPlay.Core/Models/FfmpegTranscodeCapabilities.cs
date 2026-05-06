namespace OmniPlay.Core.Models;

public sealed record FfmpegTranscodeCapabilities(
    bool IsAvailable,
    string FfmpegPath,
    IReadOnlyList<string> HardwareEncoders,
    string? PreferredHardwareEncoder,
    string? ErrorMessage,
    DateTimeOffset CheckedAt);
