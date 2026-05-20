namespace OmniPlay.Core.Models;

public sealed record EpisodeInfo(
    int Season,
    int Episode,
    string DisplayName,
    bool IsTvShow);

