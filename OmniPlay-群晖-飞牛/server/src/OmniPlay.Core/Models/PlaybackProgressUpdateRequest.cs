namespace OmniPlay.Core.Models;

public sealed record PlaybackProgressUpdateRequest(
    string VideoFileId,
    double PositionSeconds,
    double DurationSeconds,
    string UserId = "local");

