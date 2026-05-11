using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Core.Models.Network;

namespace OmniPlay.Infrastructure.Library;

public sealed class MediaServerPreflightService : IMediaServerPreflightService
{
    private readonly IMediaServerDiscoveryClient discoveryClient;

    public MediaServerPreflightService(IMediaServerDiscoveryClient discoveryClient)
    {
        this.discoveryClient = discoveryClient;
    }

    public async Task<MediaServerPreflightResult> PreScanAsync(
        MediaSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.ProtocolKind is not (MediaSourceProtocol.Plex or MediaSourceProtocol.Emby or MediaSourceProtocol.Jellyfin) ||
            !source.IsValidConfiguration())
        {
            return new MediaServerPreflightResult(false, "媒体服务器地址无效，请输入以 http:// 或 https:// 开头的地址。");
        }

        if (string.IsNullOrWhiteSpace(MediaSourceAuthConfigSerializer.DeserializeMediaServer(source.AuthConfig)?.Token))
        {
            return new MediaServerPreflightResult(false, $"{source.ProtocolLabel} 需要密码、访问令牌或 API Key 才能读取媒体列表和生成播放地址。");
        }

        try
        {
            var files = await discoveryClient.EnumerateFilesAsync(source, cancellationToken);
            var samples = files
                .Select(static file => file.FileName)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();

            var count = files.Count;
            var message = count > 0
                ? $"预扫描成功：发现 {count} 个可传输媒体条目。"
                : "预扫描成功：连接可用，但没有发现电影或剧集条目。";
            if (samples.Count > 0)
            {
                message = $"{message} 示例：{string.Join("、", samples)}";
            }

            return new MediaServerPreflightResult(true, message, count, samples);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new MediaServerPreflightResult(false, "媒体服务器预扫描超时。");
        }
        catch (HttpRequestException ex)
        {
            return new MediaServerPreflightResult(false, $"媒体服务器预扫描失败：{ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return new MediaServerPreflightResult(false, $"媒体服务器预扫描失败：{ex.Message}");
        }
    }

    public Task<IReadOnlyList<NetworkShareFolderItem>> ListLibrariesAsync(
        MediaSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.ProtocolKind is not (MediaSourceProtocol.Plex or MediaSourceProtocol.Emby or MediaSourceProtocol.Jellyfin) ||
            !source.IsValidConfiguration())
        {
            throw new InvalidOperationException("媒体服务器地址无效，请输入以 http:// 或 https:// 开头的地址。");
        }

        if (string.IsNullOrWhiteSpace(MediaSourceAuthConfigSerializer.DeserializeMediaServer(source.AuthConfig)?.Token))
        {
            throw new InvalidOperationException($"{source.ProtocolLabel} 需要密码、访问令牌或 API Key 才能读取媒体库。");
        }

        return discoveryClient.ListLibrariesAsync(source, cancellationToken);
    }
}
