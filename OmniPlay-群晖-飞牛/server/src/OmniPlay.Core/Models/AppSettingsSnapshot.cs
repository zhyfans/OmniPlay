namespace OmniPlay.Core.Models;

public sealed record AppSettingsSnapshot(
    string AppName,
    string Phase,
    TmdbSettings Tmdb,
    CacheSettings Cache,
    PlaybackSettings Playback,
    ProxySettings Proxy);

public sealed record PlaybackSettings(
    bool DirectStream = true,
    bool HlsRemux = true,
    bool Transcode = true,
    bool ShowEpisodeDetails = true);
