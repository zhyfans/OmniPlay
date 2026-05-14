namespace OmniPlay.Core.Models;

public sealed record AppSettingsUpdateRequest(
    TmdbSettings? Tmdb = null,
    CacheSettings? Cache = null,
    PlaybackSettings? Playback = null,
    ProxySettings? Proxy = null);
