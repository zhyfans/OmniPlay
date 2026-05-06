using OmniPlay.Core.Models.Entities;

namespace OmniPlay.Core.Models.Network;

public sealed class NetworkShareFolderItem
{
    public required string Name { get; init; }

    public required string ProtocolType { get; init; }

    public required string BaseUrl { get; init; }

    public string Description { get; init; } = string.Empty;

    public string? AuthConfig { get; init; }

    public bool IsStarred { get; init; }

    public string StarGlyph => IsStarred ? "★" : "☆";

    public NetworkShareFolderItem WithStarred(bool isStarred)
    {
        return new NetworkShareFolderItem
        {
            Name = Name,
            ProtocolType = ProtocolType,
            BaseUrl = BaseUrl,
            Description = Description,
            AuthConfig = AuthConfig,
            IsStarred = isStarred
        };
    }

    public MediaSource ToMediaSource()
    {
        return new MediaSource
        {
            Name = Name,
            ProtocolType = ProtocolType,
            BaseUrl = BaseUrl,
            AuthConfig = AuthConfig,
            IsEnabled = true
        };
    }
}
