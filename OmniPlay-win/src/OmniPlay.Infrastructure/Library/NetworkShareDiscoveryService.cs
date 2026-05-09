using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Core.Models.Network;

namespace OmniPlay.Infrastructure.Library;

public sealed class NetworkShareDiscoveryService : INetworkShareDiscoveryService
{
    private static readonly HttpMethod PropFindMethod = new("PROPFIND");
    private static readonly XNamespace DavNamespace = "DAV:";
    private static readonly Regex MultiSpace = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly TimeSpan MediaProbeTimeout = TimeSpan.FromSeconds(2);
    private readonly HttpClient httpClient;
    private readonly Func<CancellationToken, Task<IReadOnlyList<Uri>>> mediaServerEndpointProvider;

    public NetworkShareDiscoveryService(
        HttpClient httpClient,
        Func<CancellationToken, Task<IReadOnlyList<Uri>>>? mediaServerEndpointProvider = null)
    {
        this.httpClient = httpClient;
        this.mediaServerEndpointProvider = mediaServerEndpointProvider ?? DiscoverMediaServerEndpointCandidatesAsync;
    }

    public async Task<IReadOnlyList<NetworkSourceDiscoveryItem>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        Dictionary<string, NetworkSourceDiscoveryItem> results = new(StringComparer.OrdinalIgnoreCase)
        {
            ["webdav:manual"] = new()
            {
                Name = "手动输入 WebDAV",
                ProtocolType = "webdav",
                BaseUrl = "https://",
                Description = "输入 WebDAV 地址后登录并选择文件夹。"
            },
            ["smb:manual"] = new()
            {
                Name = "手动输入 SMB",
                ProtocolType = "smb",
                BaseUrl = @"\\server\share",
                Description = "输入 SMB 服务器或共享路径后登录并选择文件夹。"
            }
        };

        foreach (var drive in DriveInfo.GetDrives().Where(static drive => drive.DriveType == DriveType.Network))
        {
            var root = drive.RootDirectory.FullName.TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            results[$"smb:{root}"] = new NetworkSourceDiscoveryItem
            {
                Name = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? root : drive.VolumeLabel,
                ProtocolType = "smb",
                BaseUrl = root,
                Description = "Windows 已映射的网络文件夹。"
            };
        }

        foreach (var server in await ListSmbServersAsync(cancellationToken))
        {
            results[$"smb:{server}"] = new NetworkSourceDiscoveryItem
            {
                Name = server,
                ProtocolType = "smb",
                BaseUrl = server,
                Description = "预扫描到的 SMB 服务器。"
            };
        }

        foreach (var mediaServer in await DiscoverMediaServersAsync(cancellationToken))
        {
            results[$"{mediaServer.ProtocolType}:{mediaServer.BaseUrl}"] = mediaServer;
        }

        return results.Values
            .OrderBy(static item => item.ProtocolLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<NetworkShareFolderItem>> ListFoldersAsync(
        NetworkSourceDiscoveryItem source,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        return source.ProtocolKind switch
        {
            MediaSourceProtocol.WebDav => await ListWebDavFoldersAsync(source.BaseUrl, username, password, cancellationToken),
            MediaSourceProtocol.Smb => await ListSmbFoldersAsync(source.BaseUrl, username, password, cancellationToken),
            MediaSourceProtocol.Plex or MediaSourceProtocol.Emby or MediaSourceProtocol.Jellyfin => [],
            _ => []
        };
    }

    private async Task<IReadOnlyList<NetworkSourceDiscoveryItem>> DiscoverMediaServersAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Uri> candidates;
        try
        {
            candidates = await mediaServerEndpointProvider(cancellationToken);
        }
        catch
        {
            return [];
        }

        Dictionary<string, NetworkSourceDiscoveryItem> results = new(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates.DistinctBy(static uri => uri.GetLeftPart(UriPartial.Authority)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = await ProbeMediaServerAsync(candidate, cancellationToken);
            if (item is not null)
            {
                results[$"{item.ProtocolType}:{item.BaseUrl}"] = item;
            }
        }

        return results.Values
            .OrderBy(static item => item.ProtocolLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<NetworkSourceDiscoveryItem?> ProbeMediaServerAsync(Uri candidate, CancellationToken cancellationToken)
    {
        if (!candidate.IsAbsoluteUri ||
            (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(candidate.Host))
        {
            return null;
        }

        var baseUri = new Uri(candidate.GetLeftPart(UriPartial.Authority));

        if (baseUri.Port == 32400)
        {
            var plex = await ProbePlexAsync(baseUri, cancellationToken);
            if (plex is not null)
            {
                return plex;
            }
        }

        var embyCompatible = await ProbeEmbyCompatibleAsync(baseUri, cancellationToken);
        if (embyCompatible is not null)
        {
            return embyCompatible;
        }

        return await ProbePlexAsync(baseUri, cancellationToken);
    }

    private async Task<NetworkSourceDiscoveryItem?> ProbePlexAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/identity"));
        request.Headers.Accept.ParseAdd("application/xml");
        request.Headers.TryAddWithoutValidation("X-Plex-Product", "OmniPlay");
        request.Headers.TryAddWithoutValidation("X-Plex-Version", "1.0");
        request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", "omniplay-windows");
        request.Headers.TryAddWithoutValidation("X-Plex-Device-Name", "OmniPlay");

        try
        {
            using var response = await SendProbeAsync(request, cancellationToken);
            if (response is null)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var hasPlexHeader = response.Headers.Contains("X-Plex-Protocol") ||
                                response.Headers.Contains("X-Plex-Content-Original-Length");
            var hasPlexIdentity = body.Contains("MediaContainer", StringComparison.OrdinalIgnoreCase) &&
                                  body.Contains("machineIdentifier", StringComparison.OrdinalIgnoreCase);
            if (!response.IsSuccessStatusCode || (!hasPlexHeader && !hasPlexIdentity))
            {
                return null;
            }

            var normalized = MediaSourceNormalizer.NormalizeBaseUrl(MediaSourceProtocol.Plex, baseUri.AbsoluteUri);
            return new NetworkSourceDiscoveryItem
            {
                Name = ResolveServerName("Plex", baseUri),
                ProtocolType = "plex",
                BaseUrl = normalized,
                Description = $"预扫描到的 Plex 媒体服务器：{normalized}"
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<NetworkSourceDiscoveryItem?> ProbeEmbyCompatibleAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/System/Info/Public"));
        request.Headers.Accept.ParseAdd("application/json");

        try
        {
            using var response = await SendProbeAsync(request, cancellationToken);
            if (response is null || !response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var combined = body;
            if (document.RootElement.TryGetProperty("ProductName", out var productName))
            {
                combined += " " + productName.GetString();
            }
            if (document.RootElement.TryGetProperty("ServerName", out var serverName))
            {
                combined += " " + serverName.GetString();
            }

            var protocol = combined.Contains("Jellyfin", StringComparison.OrdinalIgnoreCase)
                ? MediaSourceProtocol.Jellyfin
                : combined.Contains("Emby", StringComparison.OrdinalIgnoreCase)
                    ? MediaSourceProtocol.Emby
                    : (MediaSourceProtocol?)null;
            if (protocol is null)
            {
                return null;
            }

            var normalized = MediaSourceNormalizer.NormalizeBaseUrl(protocol, baseUri.AbsoluteUri);
            return new NetworkSourceDiscoveryItem
            {
                Name = ResolveServerName(protocol == MediaSourceProtocol.Jellyfin ? "Jellyfin" : "Emby", baseUri),
                ProtocolType = protocol == MediaSourceProtocol.Jellyfin ? "jellyfin" : "emby",
                BaseUrl = normalized,
                Description = $"预扫描到的 {(protocol == MediaSourceProtocol.Jellyfin ? "Jellyfin" : "Emby")} 媒体服务器：{normalized}"
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage?> SendProbeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MediaProbeTimeout);
        try
        {
            return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<Uri>> DiscoverMediaServerEndpointCandidatesAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, Uri> results = new(StringComparer.OrdinalIgnoreCase);
        AddUri(results, "http://127.0.0.1:32400");
        AddUri(results, "http://localhost:32400");
        AddUri(results, "http://127.0.0.1:8096");
        AddUri(results, "http://localhost:8096");
        AddUri(results, "http://127.0.0.1:8097");
        AddUri(results, "http://localhost:8097");
        AddUri(results, "http://127.0.0.1:8098");
        AddUri(results, "http://localhost:8098");
        AddUri(results, "http://127.0.0.1:8099");
        AddUri(results, "http://localhost:8099");
        AddUri(results, "https://127.0.0.1:8920");
        AddUri(results, "https://localhost:8920");

        foreach (var uri in await DiscoverPlexGdmUrisAsync(cancellationToken))
        {
            AddUri(results, uri);
        }

        foreach (var uri in await DiscoverEmbyUdpUrisAsync(cancellationToken))
        {
            AddUri(results, uri);
        }

        foreach (var uri in await DiscoverSsdpLocationUrisAsync(cancellationToken))
        {
            AddUri(results, uri);
        }

        return results.Values.ToList();
    }

    private static async Task<IReadOnlyList<Uri>> DiscoverPlexGdmUrisAsync(CancellationToken cancellationToken)
    {
        List<Uri> results = [];
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork)
            {
                EnableBroadcast = true
            };
            var payload = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.0\r\n\r\n");
            await udp.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Broadcast, 32414));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            while (!timeout.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await udp.ReceiveAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var text = Encoding.UTF8.GetString(received.Buffer);
                var headers = ParseHeaderBlock(text);
                if (!LooksLikePlexGdmResponse(text, headers))
                {
                    continue;
                }

                if (headers.TryGetValue("Port", out var portText) &&
                    int.TryParse(portText, out var port) &&
                    Uri.TryCreate($"http://{received.RemoteEndPoint.Address}:{port}", UriKind.Absolute, out var uri))
                {
                    results.Add(uri);
                }
            }
        }
        catch
        {
        }

        return results;
    }

    private static async Task<IReadOnlyList<Uri>> DiscoverEmbyUdpUrisAsync(CancellationToken cancellationToken)
    {
        List<Uri> results = [];
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork)
            {
                EnableBroadcast = true
            };
            var payload = Encoding.UTF8.GetBytes("who is EmbyServer?");
            foreach (var endpoint in GetUdpBroadcastEndpoints(7359))
            {
                await udp.SendAsync(payload, payload.Length, endpoint);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            while (!timeout.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await udp.ReceiveAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var text = Encoding.UTF8.GetString(received.Buffer);
                if (TryCreateEmbyUdpDiscoveryUri(text, received.RemoteEndPoint.Address, out var uri))
                {
                    results.Add(uri);
                }
            }
        }
        catch
        {
        }

        return results;
    }

    private static IReadOnlyList<IPEndPoint> GetUdpBroadcastEndpoints(int port)
    {
        Dictionary<string, IPEndPoint> endpoints = new(StringComparer.OrdinalIgnoreCase)
        {
            [IPAddress.Broadcast.ToString()] = new(IPAddress.Broadcast, port)
        };

        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                        unicast.IPv4Mask is null ||
                        CreateBroadcastAddress(unicast.Address, unicast.IPv4Mask) is not { } broadcast)
                    {
                        continue;
                    }

                    endpoints[broadcast.ToString()] = new IPEndPoint(broadcast, port);
                }
            }
        }
        catch
        {
        }

        return endpoints.Values.ToList();
    }

    private static IPAddress? CreateBroadcastAddress(IPAddress address, IPAddress mask)
    {
        var addressBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        if (addressBytes.Length != 4 || maskBytes.Length != 4)
        {
            return null;
        }

        var broadcastBytes = new byte[4];
        for (var i = 0; i < broadcastBytes.Length; i++)
        {
            broadcastBytes[i] = (byte)(addressBytes[i] | (maskBytes[i] ^ 0xFF));
        }

        return new IPAddress(broadcastBytes);
    }

    private static bool TryCreateEmbyUdpDiscoveryUri(string text, IPAddress remoteAddress, out Uri uri)
    {
        uri = null!;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (TryGetJsonString(document.RootElement, "Address", out var address) &&
                Uri.TryCreate(address, UriKind.Absolute, out var discovered) &&
                (discovered.Scheme == Uri.UriSchemeHttp || discovered.Scheme == Uri.UriSchemeHttps))
            {
                uri = discovered;
                return true;
            }

            if ((TryGetJsonString(document.RootElement, "Id", out _) ||
                 TryGetJsonString(document.RootElement, "Name", out _)) &&
                Uri.TryCreate($"http://{remoteAddress}:8096", UriKind.Absolute, out var fallback))
            {
                uri = fallback;
                return true;
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    private static bool TryGetJsonString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) ||
                property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            value = property.Value.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        return false;
    }

    private static async Task<IReadOnlyList<Uri>> DiscoverSsdpLocationUrisAsync(CancellationToken cancellationToken)
    {
        List<Uri> results = [];
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var payload = Encoding.ASCII.GetBytes(
                "M-SEARCH * HTTP/1.1\r\n" +
                "HOST: 239.255.255.250:1900\r\n" +
                "MAN: \"ssdp:discover\"\r\n" +
                "MX: 1\r\n" +
                "ST: ssdp:all\r\n\r\n");
            await udp.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            while (!timeout.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await udp.ReceiveAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var text = Encoding.UTF8.GetString(received.Buffer);
                var headers = ParseHeaderBlock(text);
                if (headers.TryGetValue("Location", out var location) &&
                    Uri.TryCreate(location, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    results.Add(new Uri(uri.GetLeftPart(UriPartial.Authority)));
                }
            }
        }
        catch
        {
        }

        return results;
    }

    private static Dictionary<string, string> ParseHeaderBlock(string text)
    {
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = rawLine.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            headers[rawLine[..separator].Trim()] = rawLine[(separator + 1)..].Trim();
        }

        return headers;
    }

    private static bool LooksLikePlexGdmResponse(string text, IReadOnlyDictionary<string, string> headers)
    {
        return text.Contains("Plex Media Server", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("plex/media-server", StringComparison.OrdinalIgnoreCase) ||
               headers.TryGetValue("Product", out var product) && product.Contains("Plex", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddUri(IDictionary<string, Uri> results, string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            AddUri(results, parsed);
        }
    }

    private static void AddUri(IDictionary<string, Uri> results, Uri uri)
    {
        if ((!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return;
        }

        var authority = uri.GetLeftPart(UriPartial.Authority);
        results[authority] = new Uri(authority);
    }

    private static string ResolveServerName(string protocolLabel, Uri baseUri)
    {
        return $"{protocolLabel} · {baseUri.Host}";
    }

    private async Task<IReadOnlyList<NetworkShareFolderItem>> ListWebDavFoldersAsync(
        string baseUrl,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var source = new MediaSource
        {
            Name = ResolveWebDavName(baseUrl),
            ProtocolType = "webdav",
            BaseUrl = baseUrl,
            AuthConfig = MediaSourceAuthConfigSerializer.SerializeWebDav(new WebDavAuthConfig(username, password))
        };

        if (!source.IsValidConfiguration() ||
            !Uri.TryCreate(AppendTrailingSlash(source.GetNormalizedBaseUrl()), UriKind.Absolute, out var rootUri))
        {
            return [];
        }

        List<NetworkShareFolderItem> folders =
        [
            new()
            {
                Name = source.Name,
                ProtocolType = "webdav",
                BaseUrl = source.GetNormalizedBaseUrl(),
                Description = source.GetNormalizedBaseUrl(),
                AuthConfig = source.AuthConfig
            }
        ];

        using var request = BuildWebDavRequest(
            rootUri,
            MediaSourceAuthConfigSerializer.DeserializeWebDav(source.AuthConfig));
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        var rootKey = NormalizeUri(rootUri);

        foreach (var folderUri in ParseWebDavFolders(document, rootUri))
        {
            if (string.Equals(NormalizeUri(folderUri), rootKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = folderUri.AbsoluteUri.TrimEnd('/');
            folders.Add(new NetworkShareFolderItem
            {
                Name = ResolveWebDavName(url),
                ProtocolType = "webdav",
                BaseUrl = url,
                Description = url,
                AuthConfig = source.AuthConfig
            });
        }

        return folders
            .GroupBy(static folder => folder.BaseUrl, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static folder => folder.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<NetworkShareFolderItem>> ListSmbFoldersAsync(
        string baseUrl,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var normalizedBaseUrl = MediaSourceNormalizer.NormalizeBaseUrl(MediaSourceProtocol.Smb, baseUrl);
        if (!normalizedBaseUrl.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return [];
        }

        var authConfig = MediaSourceAuthConfigSerializer.SerializeWebDav(new WebDavAuthConfig(username, password));
        var parts = normalizedBaseUrl.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            using var ipcConnection = SmbNetworkConnection.Connect($@"{normalizedBaseUrl.TrimEnd('\\')}\IPC$", username, password);
            return (await ListSmbSharesAsync(normalizedBaseUrl, cancellationToken))
                .Select(share => new NetworkShareFolderItem
                {
                    Name = Path.GetFileName(share),
                    ProtocolType = "smb",
                    BaseUrl = share,
                    Description = share,
                    AuthConfig = authConfig
                })
                .ToList();
        }

        using var connection = SmbNetworkConnection.Connect(normalizedBaseUrl, username, password);
        List<NetworkShareFolderItem> folders =
        [
            new()
            {
                Name = Path.GetFileName(normalizedBaseUrl.TrimEnd('\\')) ?? normalizedBaseUrl,
                ProtocolType = "smb",
                BaseUrl = normalizedBaseUrl,
                Description = normalizedBaseUrl,
                AuthConfig = authConfig
            }
        ];

        if (Directory.Exists(normalizedBaseUrl))
        {
            foreach (var directory in Directory.EnumerateDirectories(normalizedBaseUrl).Take(200))
            {
                folders.Add(new NetworkShareFolderItem
                {
                    Name = Path.GetFileName(directory),
                    ProtocolType = "smb",
                    BaseUrl = directory.TrimEnd('\\'),
                    Description = directory.TrimEnd('\\'),
                    AuthConfig = authConfig
                });
            }
        }

        return folders;
    }

    private static async Task<IReadOnlyList<string>> ListSmbServersAsync(CancellationToken cancellationToken)
    {
        var output = await RunNetViewAsync("view", cancellationToken);
        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(static line => line.StartsWith(@"\\", StringComparison.Ordinal))
            .Select(static line => MultiSpace.Split(line)[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static line => line, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<IReadOnlyList<string>> ListSmbSharesAsync(string server, CancellationToken cancellationToken)
    {
        var output = await RunNetViewAsync($"view {server}", cancellationToken);
        List<string> shares = [];
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 ||
                line.StartsWith("Share name", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("共享名", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("The command", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("命令成功", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            var shareName = MultiSpace.Split(line)[0];
            if (shareName.Length == 0 || shareName.EndsWith('$'))
            {
                continue;
            }

            shares.Add($@"{server.TrimEnd('\\')}\{shareName}");
        }

        return shares
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static share => share, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<string> RunNetViewAsync(string arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "net",
                Arguments = arguments,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });

            if (process is null)
            {
                return string.Empty;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                return await outputTask;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return string.Empty;
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    private static HttpRequestMessage BuildWebDavRequest(Uri directoryUri, WebDavAuthConfig? authConfig)
    {
        var request = new HttpRequestMessage(PropFindMethod, directoryUri)
        {
            Content = new StringContent(
                """
                <?xml version="1.0" encoding="utf-8"?>
                <d:propfind xmlns:d="DAV:">
                  <d:prop>
                    <d:resourcetype />
                  </d:prop>
                </d:propfind>
                """,
                Encoding.UTF8,
                "application/xml")
        };

        request.Headers.TryAddWithoutValidation("Depth", "1");
        if (authConfig is not null &&
            (!string.IsNullOrWhiteSpace(authConfig.Username) || !string.IsNullOrEmpty(authConfig.Password)))
        {
            var raw = $"{authConfig.Username}:{authConfig.Password}";
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        }

        return request;
    }

    private static IEnumerable<Uri> ParseWebDavFolders(XDocument document, Uri requestUri)
    {
        foreach (var responseElement in document.Descendants(DavNamespace + "response"))
        {
            var href = responseElement.Element(DavNamespace + "href")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(requestUri, href, out var resourceUri))
            {
                continue;
            }

            var isDirectory = responseElement
                                  .Descendants(DavNamespace + "resourcetype")
                                  .Any(static element => element.Element(DavNamespace + "collection") is not null)
                              || href.EndsWith("/", StringComparison.Ordinal);
            if (isDirectory)
            {
                yield return EnsureDirectoryUri(resourceUri);
            }
        }
    }

    private static Uri EnsureDirectoryUri(Uri resourceUri)
    {
        var absoluteUri = resourceUri.AbsoluteUri;
        return absoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? resourceUri
            : new Uri($"{absoluteUri}/");
    }

    private static string AppendTrailingSlash(string value)
    {
        return value.EndsWith("/", StringComparison.Ordinal) ? value : $"{value}/";
    }

    private static string NormalizeUri(Uri uri)
    {
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static string ResolveWebDavName(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return "WebDAV";
        }

        var lastSegment = uri.Segments.LastOrDefault()?.Trim('/');
        return string.IsNullOrWhiteSpace(lastSegment)
            ? uri.Host
            : Uri.UnescapeDataString(lastSegment);
    }
}
