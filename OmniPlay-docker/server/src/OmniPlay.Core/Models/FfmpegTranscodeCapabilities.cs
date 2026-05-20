namespace OmniPlay.Core.Models;

public sealed record FfmpegTranscodeCapabilities(
    bool IsAvailable,
    string FfmpegPath,
    IReadOnlyList<string> HardwareEncoders,
    string? PreferredHardwareEncoder,
    IReadOnlyList<string> HardwareDecoders,
    string? PreferredHardwareDecoder,
    IReadOnlyList<string> HardwareAccelerators,
    string? ErrorMessage,
    DateTimeOffset CheckedAt,
    IReadOnlyList<string>? DetectedHardwareEncoders = null,
    IReadOnlyList<string>? DetectedHardwareDecoders = null,
    IReadOnlyList<string>? DetectedHardwareAccelerators = null,
    IReadOnlyDictionary<string, string>? HardwareEncoderProbeErrors = null);
