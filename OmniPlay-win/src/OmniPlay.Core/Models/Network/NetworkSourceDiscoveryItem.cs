using OmniPlay.Core.Models.Entities;

namespace OmniPlay.Core.Models.Network;

public sealed class NetworkSourceDiscoveryItem
{
    public required string Name { get; init; }

    public required string ProtocolType { get; init; }

    public required string BaseUrl { get; init; }

    public string Description { get; init; } = string.Empty;

    public MediaSourceProtocol? ProtocolKind =>
        ProtocolType.Trim().ToLowerInvariant() switch
        {
            "webdav" => MediaSourceProtocol.WebDav,
            "smb" => MediaSourceProtocol.Smb,
            "plex" => MediaSourceProtocol.Plex,
            "emby" => MediaSourceProtocol.Emby,
            "jellyfin" => MediaSourceProtocol.Jellyfin,
            _ => null
        };

    public string ProtocolLabel => ProtocolKind switch
    {
        MediaSourceProtocol.WebDav => "WebDAV",
        MediaSourceProtocol.Smb => "SMB",
        MediaSourceProtocol.Plex => "Plex",
        MediaSourceProtocol.Emby => "Emby",
        MediaSourceProtocol.Jellyfin => "Jellyfin",
        _ => ProtocolType
    };
}
