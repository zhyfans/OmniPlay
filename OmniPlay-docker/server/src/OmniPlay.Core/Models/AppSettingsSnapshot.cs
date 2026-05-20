namespace OmniPlay.Core.Models;

public sealed record AppSettingsSnapshot(
    string AppName,
    string Phase,
    TmdbSettings Tmdb,
    CacheSettings Cache,
    PlaybackSettings Playback,
    ProxySettings Proxy,
    AutomationSettings Automation);

public sealed record PlaybackSettings(
    bool DirectStream = true,
    bool HlsRemux = true,
    bool Transcode = true,
    bool ShowEpisodeDetails = true,
    string PlaybackQualityPreference = "auto",
    string DefaultAudioLanguage = "smart",
    string DefaultSubtitleLanguage = "zh");

public sealed record AutomationSettings(
    bool ScheduledLibraryRefreshEnabled = false,
    int ScheduledLibraryRefreshIntervalHours = 24);
