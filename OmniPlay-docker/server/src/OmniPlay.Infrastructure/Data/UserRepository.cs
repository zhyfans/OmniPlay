using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.Data;

public sealed class UserRepository : IUserRepository
{
    private const int PasswordIterations = 210_000;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
    private readonly SqliteDatabase database;

    public UserRepository(SqliteDatabase database)
    {
        this.database = database;
    }

    public async Task<bool> HasUsersAsync(CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM users LIMIT 1;";
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<AuthUser?> GetUserBySessionTokenAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT u.id, u.username, u.role
            FROM api_tokens t
            JOIN users u ON u.id = t.user_id
            WHERE t.token_hash = $tokenHash
              AND t.revoked_at IS NULL
              AND (t.expires_at IS NULL OR t.expires_at > $now)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$tokenHash", HashToken(token));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AuthUser(reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    public async Task<AuthSession> RegisterFirstAdminAsync(
        AuthRequest request,
        CancellationToken cancellationToken = default)
    {
        var username = NormalizeUsername(request.Username);
        ValidatePassword(request.Password);

        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM users;";
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken)) > 0)
            {
                throw new InvalidOperationException("管理员账号已存在，请直接登录。");
            }
        }

        var now = DateTimeOffset.UtcNow;
        var user = new AuthUser(Guid.NewGuid().ToString("N"), username, "admin");
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO users (id, username, password_hash, role, created_at, updated_at)
                VALUES ($id, $username, $passwordHash, $role, $createdAt, $updatedAt);
                """;
            insert.Parameters.AddWithValue("$id", user.Id);
            insert.Parameters.AddWithValue("$username", user.Username);
            insert.Parameters.AddWithValue("$passwordHash", HashPassword(request.Password));
            insert.Parameters.AddWithValue("$role", user.Role);
            insert.Parameters.AddWithValue("$createdAt", now.ToString("O"));
            insert.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        var session = await CreateSessionAsync(connection, transaction, user, cancellationToken);
        transaction.Commit();
        return session;
    }

    public async Task<AuthSession?> LoginAsync(
        AuthRequest request,
        CancellationToken cancellationToken = default)
    {
        var username = NormalizeUsername(request.Username);
        using var connection = database.OpenConnection();
        using var lookup = connection.CreateCommand();
        lookup.CommandText = """
            SELECT id, username, password_hash, role
            FROM users
            WHERE username = $username
            LIMIT 1;
            """;
        lookup.Parameters.AddWithValue("$username", username);

        using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var passwordHash = reader.GetString(2);
        if (!VerifyPassword(request.Password, passwordHash))
        {
            return null;
        }

        var user = new AuthUser(reader.GetString(0), reader.GetString(1), reader.GetString(3));
        using var transaction = connection.BeginTransaction();
        var session = await CreateSessionAsync(connection, transaction, user, cancellationToken);
        transaction.Commit();
        return session;
    }

    public async Task RevokeSessionAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE api_tokens
            SET revoked_at = $revokedAt
            WHERE token_hash = $tokenHash
              AND revoked_at IS NULL;
            """;
        command.Parameters.AddWithValue("$tokenHash", HashToken(token));
        command.Parameters.AddWithValue("$revokedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<AuthSession> CreateSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthUser user,
        CancellationToken cancellationToken)
    {
        var token = CreateToken();
        var expiresAt = DateTimeOffset.UtcNow.Add(SessionLifetime);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO api_tokens (id, user_id, token_hash, name, expires_at, created_at, revoked_at)
            VALUES ($id, $userId, $tokenHash, $name, $expiresAt, $createdAt, NULL);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$tokenHash", HashToken(token));
        command.Parameters.AddWithValue("$name", "web");
        command.Parameters.AddWithValue("$expiresAt", expiresAt.ToString("O"));
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new AuthSession(user, token, expiresAt);
    }

    private static string NormalizeUsername(string username)
    {
        var normalized = username.Trim();
        if (normalized.Length is < 3 or > 64)
        {
            throw new ArgumentException("用户名长度需要在 3 到 64 个字符之间。");
        }

        return normalized;
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
        {
            throw new ArgumentException("密码至少需要 8 个字符。");
        }
    }

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            PasswordIterations,
            HashAlgorithmName.SHA256,
            32);
        return $"pbkdf2-sha256${PasswordIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string encoded)
    {
        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations))
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string CreateToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    private static string HashToken(string token)
    {
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }
}
