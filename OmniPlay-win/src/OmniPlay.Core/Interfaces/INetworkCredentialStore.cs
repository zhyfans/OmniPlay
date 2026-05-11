using OmniPlay.Core.Models.Entities;

namespace OmniPlay.Core.Interfaces;

public sealed record NetworkCredentialEntry(
    string ProtocolType,
    string BaseUrl,
    string Username,
    string Password,
    DateTimeOffset LastUsed);

public interface INetworkCredentialStore
{
    NetworkCredentialEntry? FindBest(MediaSourceProtocol protocol, string baseUrl);

    NetworkCredentialEntry? FindLatest();

    void Save(MediaSourceProtocol protocol, string baseUrl, string username, string password);
}
