using System.Text.Json;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models.Entities;

namespace OmniPlay.Infrastructure.Data;

public sealed class NetworkCredentialStore : INetworkCredentialStore
{
    private const int MaximumEntries = 20;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string filePath;
    private readonly object gate = new();

    public NetworkCredentialStore(IStoragePaths storagePaths)
    {
        filePath = Path.Combine(storagePaths.SettingsDirectory, "network-credentials.json");
    }

    public NetworkCredentialEntry? FindBest(MediaSourceProtocol protocol, string baseUrl)
    {
        var normalizedProtocol = NormalizeProtocol(protocol);
        var normalizedBaseUrl = NormalizeBaseUrl(protocol, baseUrl);
        lock (gate)
        {
            var entries = LoadEntries();
            var exact = entries
                .Where(entry => string.Equals(entry.ProtocolType, normalizedProtocol, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(entry => string.Equals(entry.BaseUrl, normalizedBaseUrl, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return ToPublicEntry(exact);
            }

            var endpointMatch = entries
                .Where(entry => string.Equals(entry.ProtocolType, normalizedProtocol, StringComparison.OrdinalIgnoreCase))
                .Where(entry => IsSameEndpoint(protocol, entry.BaseUrl, normalizedBaseUrl))
                .OrderByDescending(static entry => entry.LastUsed)
                .FirstOrDefault();
            if (endpointMatch is not null)
            {
                return ToPublicEntry(endpointMatch);
            }

            return null;
        }
    }

    public NetworkCredentialEntry? FindLatest()
    {
        lock (gate)
        {
            var latest = LoadEntries()
                .OrderByDescending(static entry => entry.LastUsed)
                .FirstOrDefault();
            return latest is null ? null : ToPublicEntry(latest);
        }
    }

    public void Save(MediaSourceProtocol protocol, string baseUrl, string username, string password)
    {
        var normalizedProtocol = NormalizeProtocol(protocol);
        var normalizedBaseUrl = NormalizeBaseUrl(protocol, baseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl) ||
            string.IsNullOrWhiteSpace(username) && string.IsNullOrEmpty(password))
        {
            return;
        }

        try
        {
            lock (gate)
            {
                var entries = LoadEntries()
                    .Where(entry => !string.Equals(entry.ProtocolType, normalizedProtocol, StringComparison.OrdinalIgnoreCase) ||
                                    !string.Equals(entry.BaseUrl, normalizedBaseUrl, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(static entry => entry.LastUsed)
                    .ToList();
                entries.Insert(
                    0,
                    new StoredNetworkCredentialEntry
                    {
                        ProtocolType = normalizedProtocol,
                        BaseUrl = normalizedBaseUrl,
                        Username = username.Trim(),
                        ProtectedPassword = MediaSourceAuthConfigProtector.ProtectForStorage(password) ?? string.Empty,
                        LastUsed = DateTimeOffset.UtcNow
                    });
                if (entries.Count > MaximumEntries)
                {
                    entries = entries.Take(MaximumEntries).ToList();
                }
                SaveEntries(entries);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    private List<StoredNetworkCredentialEntry> LoadEntries()
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return [];
            }

            using var stream = File.OpenRead(filePath);
            return JsonSerializer.Deserialize<List<StoredNetworkCredentialEntry>>(stream, SerializerOptions) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private void SaveEntries(IReadOnlyList<StoredNetworkCredentialEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        using var stream = File.Create(filePath);
        JsonSerializer.Serialize(stream, entries, SerializerOptions);
    }

    private static NetworkCredentialEntry ToPublicEntry(StoredNetworkCredentialEntry entry)
    {
        return new NetworkCredentialEntry(
            entry.ProtocolType,
            entry.BaseUrl,
            entry.Username,
            MediaSourceAuthConfigProtector.UnprotectFromStorage(entry.ProtectedPassword) ?? string.Empty,
            entry.LastUsed);
    }

    private static string NormalizeProtocol(MediaSourceProtocol protocol)
    {
        return protocol switch
        {
            MediaSourceProtocol.Smb => "smb",
            MediaSourceProtocol.WebDav => "webdav",
            _ => protocol.ToString().ToLowerInvariant()
        };
    }

    private static string NormalizeBaseUrl(MediaSourceProtocol protocol, string baseUrl)
    {
        return MediaSourceNormalizer.NormalizeBaseUrl(protocol, baseUrl);
    }

    private static bool IsSameEndpoint(MediaSourceProtocol protocol, string left, string right)
    {
        return protocol switch
        {
            MediaSourceProtocol.WebDav => IsSameWebDavEndpoint(left, right),
            MediaSourceProtocol.Smb => string.Equals(ResolveSmbServer(left), ResolveSmbServer(right), StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool IsSameWebDavEndpoint(string left, string right)
    {
        if (!Uri.TryCreate(left, UriKind.Absolute, out var leftUri) ||
            !Uri.TryCreate(right, UriKind.Absolute, out var rightUri))
        {
            return false;
        }

        return string.Equals(leftUri.Scheme, rightUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(leftUri.Host, rightUri.Host, StringComparison.OrdinalIgnoreCase) &&
               leftUri.Port == rightUri.Port;
    }

    private static string ResolveSmbServer(string path)
    {
        var normalized = path.Trim().Replace('/', '\\').Trim('\\');
        var separatorIndex = normalized.IndexOf('\\');
        return separatorIndex < 0 ? normalized : normalized[..separatorIndex];
    }

    private sealed class StoredNetworkCredentialEntry
    {
        public string ProtocolType { get; set; } = string.Empty;

        public string BaseUrl { get; set; } = string.Empty;

        public string Username { get; set; } = string.Empty;

        public string ProtectedPassword { get; set; } = string.Empty;

        public DateTimeOffset LastUsed { get; set; }
    }
}
