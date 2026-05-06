namespace OmniPlay.Core.Models;

public sealed record HlsPlaybackAsset(
    string FullPath,
    string ContentType,
    bool EnableRangeProcessing);
