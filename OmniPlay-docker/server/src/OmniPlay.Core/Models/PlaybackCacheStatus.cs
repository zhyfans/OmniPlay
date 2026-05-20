namespace OmniPlay.Core.Models;

public sealed record PlaybackCacheStatus(
    string VideoFileId,
    bool IsRemote,
    bool IsReady,
    bool IsDownloading,
    bool CanCancel,
    long? TotalBytes,
    long DownloadedBytes,
    double? Percent,
    string State,
    string? ErrorMessage,
    bool CanStreamDirect = false);
