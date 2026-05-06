namespace OmniPlay.Core.Models;

public sealed record PlaybackDiagnostics(
    string FileId,
    string FileName,
    bool IsRemote,
    string SourceKind,
    string RequestedQuality,
    int? RequestedAudioTrackIndex,
    string RequestedSubtitleMode,
    string? RequestedSubtitleId,
    bool HardwareRequested,
    string BaseMode,
    string EffectiveMode,
    string Reason,
    bool RequiresFullCache,
    bool UsesWebDavRangeProxy,
    bool UsesDirectStream,
    bool UsesHls,
    bool UsesTranscode,
    bool BurnsSubtitle,
    string? DirectStreamUrl,
    string? HlsManifestUrl,
    HlsPlaybackProfile? HlsProfile,
    string? FfmpegCommandPreview,
    PlaybackCacheStatus? CacheStatus,
    FfmpegTranscodeCapabilities? Capabilities,
    IReadOnlyList<PlaybackDiagnosticStep> Steps);

public sealed record PlaybackDiagnosticStep(
    string Key,
    string Label,
    string Status,
    string Detail);
