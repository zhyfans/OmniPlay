namespace OmniPlay.Core.Models;

public sealed record AuthUser(
    string Id,
    string Username,
    string Role);

public sealed record AuthStatus(
    bool IsSetupRequired,
    bool IsAuthenticated,
    string? Username,
    string? Role);

public sealed record AuthRequest(
    string Username,
    string Password);

public sealed record AuthSession(
    AuthUser User,
    string Token,
    DateTimeOffset ExpiresAt);
