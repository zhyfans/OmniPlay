using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface IUserRepository
{
    Task<bool> HasUsersAsync(CancellationToken cancellationToken = default);

    Task<AuthUser?> GetUserBySessionTokenAsync(
        string token,
        CancellationToken cancellationToken = default);

    Task<AuthSession> RegisterFirstAdminAsync(
        AuthRequest request,
        CancellationToken cancellationToken = default);

    Task<AuthSession?> LoginAsync(
        AuthRequest request,
        CancellationToken cancellationToken = default);

    Task RevokeSessionAsync(
        string token,
        CancellationToken cancellationToken = default);
}
