using System.Net;
using System.Globalization;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.Network;

public static class ProxyHttpMessageHandlerFactory
{
    private static readonly HashSet<string> SupportedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        Uri.UriSchemeHttp,
        Uri.UriSchemeHttps,
        "socks",
        "socks4",
        "socks4a",
        "socks5"
    };

    public static HttpClient CreateHttpClient(ProxySettings settings, bool forceProxy = false)
    {
        return new HttpClient(CreateHandler(settings, requestUri: null, forceProxy), disposeHandler: true);
    }

    public static SocketsHttpHandler CreateHandler(
        ProxySettings settings,
        Uri? requestUri,
        bool forceProxy = false)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

        if (ShouldUseProxy(settings, requestUri, forceProxy) &&
            ResolveEffectiveProxyUri(settings) is { } proxyUri)
        {
            var proxy = new FixedWebProxy(proxyUri);
            var credentials = ResolveCredentials(settings, proxyUri);
            if (credentials is not null)
            {
                proxy.Credentials = credentials;
            }

            handler.Proxy = proxy;
            handler.UseProxy = true;
        }
        else
        {
            handler.UseProxy = false;
        }

        return handler;
    }

    public static string BuildHandlerKey(
        ProxySettings settings,
        Uri? requestUri,
        bool forceProxy = false)
    {
        if (!ShouldUseProxy(settings, requestUri, forceProxy) ||
            ResolveEffectiveProxyUri(settings) is not { } proxyUri)
        {
            return "direct";
        }

        var credentials = ResolveCredentials(settings, proxyUri);
        return string.Join(
            '|',
            "proxy",
            proxyUri.AbsoluteUri,
            credentials?.UserName ?? string.Empty,
            credentials?.Password ?? string.Empty,
            settings.BypassList.Trim());
    }

    public static bool TryCreateProxyUri(ProxySettings settings, out Uri? proxyUri)
    {
        proxyUri = null;
        var rawUrl = settings.Url.Trim();
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }

        var candidate = rawUrl.Contains("://", StringComparison.Ordinal)
            ? rawUrl
            : $"http://{rawUrl}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !SupportedSchemes.Contains(uri.Scheme))
        {
            return false;
        }

        proxyUri = NormalizeProxyUriScheme(uri);
        return true;
    }

    public static IReadOnlyList<Uri> ResolveProxyUriCandidates(ProxySettings settings)
    {
        if (!TryCreateProxyUri(settings, out var proxyUri) || proxyUri is null)
        {
            return [];
        }

        List<Uri> candidates = [];
        var shouldRewriteContainerLoopback = IsRunningInContainer()
            && IsLoopbackProxyHost(proxyUri.Host)
            && !IsDockerHostNetworkEnabled();
        if (shouldRewriteContainerLoopback)
        {
            if (CanResolveHost("host.docker.internal"))
            {
                candidates.Add(ReplaceHost(proxyUri, "host.docker.internal"));
            }

            if (TryReadConfiguredDockerHostGateway(out var configuredGatewayHost))
            {
                candidates.Add(ReplaceHost(proxyUri, configuredGatewayHost));
            }

            if (TryReadDockerDefaultGateway(out var gatewayHost))
            {
                candidates.Add(ReplaceHost(proxyUri, gatewayHost));
            }
        }

        if (!shouldRewriteContainerLoopback)
        {
            candidates.Add(proxyUri);
        }

        return candidates
            .GroupBy(static uri => uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
    }

    public static string DisplayProxyUrl(ProxySettings settings)
    {
        if (!TryCreateProxyUri(settings, out var proxyUri) || proxyUri is null)
        {
            return settings.Url.Trim();
        }

        var builder = new UriBuilder(proxyUri.Scheme, proxyUri.Host);
        if (!proxyUri.IsDefaultPort)
        {
            builder.Port = proxyUri.Port;
        }

        return builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private static bool ShouldUseProxy(ProxySettings settings, Uri? requestUri, bool forceProxy)
    {
        if (!settings.IsEnabled || !TryCreateProxyUri(settings, out _))
        {
            return false;
        }

        return forceProxy || !ShouldBypassProxy(settings, requestUri);
    }

    private static Uri? ResolveEffectiveProxyUri(ProxySettings settings)
    {
        return ResolveProxyUriCandidates(settings).FirstOrDefault();
    }

    private static Uri NormalizeProxyUriScheme(Uri uri)
    {
        if (!string.Equals(uri.Scheme, "socks", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = "socks5"
        };
        return builder.Uri;
    }

    private static NetworkCredential? ResolveCredentials(ProxySettings settings, Uri proxyUri)
    {
        if (!string.IsNullOrWhiteSpace(settings.Username) || !string.IsNullOrWhiteSpace(settings.Password))
        {
            return new NetworkCredential(settings.Username.Trim(), settings.Password.Trim());
        }

        if (string.IsNullOrWhiteSpace(proxyUri.UserInfo))
        {
            return null;
        }

        var parts = proxyUri.UserInfo.Split(':', 2);
        var username = Uri.UnescapeDataString(parts[0]);
        var password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        return string.IsNullOrWhiteSpace(username) && string.IsNullOrWhiteSpace(password)
            ? null
            : new NetworkCredential(username, password);
    }

    private static Uri ReplaceHost(Uri uri, string host)
    {
        var builder = new UriBuilder(uri)
        {
            Host = host
        };
        return builder.Uri;
    }

    private static bool IsLoopbackProxyHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    }

    private static bool IsRunningInContainer()
    {
        return string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
               || File.Exists("/.dockerenv")
               || File.Exists("/run/.containerenv");
    }

    private static bool IsDockerHostNetworkEnabled()
    {
        var value = Environment.GetEnvironmentVariable("OMNIPLAY_DOCKER_HOST_NETWORK");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanResolveHost(string host)
    {
        try
        {
            return Dns.GetHostAddresses(host).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadDockerDefaultGateway(out string gatewayHost)
    {
        gatewayHost = string.Empty;
        const string routePath = "/proc/net/route";
        if (!File.Exists(routePath))
        {
            return false;
        }

        try
        {
            foreach (var line in File.ReadLines(routePath).Skip(1))
            {
                var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 3 || !string.Equals(parts[1], "00000000", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!uint.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rawGateway))
                {
                    continue;
                }

                var bytes = BitConverter.GetBytes(rawGateway);
                gatewayHost = new IPAddress(bytes).ToString();
                return !string.IsNullOrWhiteSpace(gatewayHost);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static bool TryReadConfiguredDockerHostGateway(out string gatewayHost)
    {
        gatewayHost = Environment.GetEnvironmentVariable("OMNIPLAY_DOCKER_HOST_GATEWAY")?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(gatewayHost);
    }

    private static bool ShouldBypassProxy(ProxySettings settings, Uri? requestUri)
    {
        if (requestUri is null || string.IsNullOrWhiteSpace(requestUri.Host))
        {
            return true;
        }

        var host = requestUri.Host.Trim('[', ']').Trim().ToLowerInvariant();
        if (IsImplicitBypassHost(host))
        {
            return true;
        }

        return SplitBypassList(settings.BypassList).Any(pattern => MatchesHostPattern(host, pattern));
    }

    private static IEnumerable<string> SplitBypassList(string value)
    {
        return value
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeBypassPattern)
            .Where(static pattern => !string.IsNullOrWhiteSpace(pattern));
    }

    private static string NormalizeBypassPattern(string value)
    {
        var pattern = value.Trim().ToLowerInvariant();
        if (pattern.Contains("://", StringComparison.Ordinal) &&
            Uri.TryCreate(pattern, UriKind.Absolute, out var uri))
        {
            return uri.Host.Trim('[', ']').ToLowerInvariant();
        }

        var slashIndex = pattern.IndexOf('/', StringComparison.Ordinal);
        if (slashIndex >= 0)
        {
            pattern = pattern[..slashIndex];
        }

        var portIndex = pattern.LastIndexOf(':');
        if (portIndex > 0 && pattern.IndexOf(':') == portIndex)
        {
            pattern = pattern[..portIndex];
        }

        return pattern.Trim('[', ']');
    }

    private static bool MatchesHostPattern(string host, string pattern)
    {
        if (pattern == "*")
        {
            return true;
        }

        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = pattern[1..];
            return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(host, suffix.TrimStart('.'), StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.StartsWith(".", StringComparison.Ordinal))
        {
            return host.EndsWith(pattern, StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.Contains('*', StringComparison.Ordinal))
        {
            return MatchesWildcard(host, pattern);
        }

        return string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase)
               || host.EndsWith($".{pattern}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesWildcard(string host, string pattern)
    {
        var parts = pattern.Split('*', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return true;
        }

        var position = 0;
        if (!pattern.StartsWith('*') &&
            !host.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var part in parts)
        {
            var index = host.IndexOf(part, position, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            position = index + part.Length;
        }

        return pattern.EndsWith('*') ||
               host.EndsWith(parts[^1], StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImplicitBypassHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            return !host.Contains('.', StringComparison.Ordinal);
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   bytes[0] == 127 ||
                   bytes[0] == 169 && bytes[1] == 254 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                   bytes[0] == 192 && bytes[1] == 168;
        }

        var addressBytes = address.GetAddressBytes();
        return address.IsIPv6LinkLocal ||
               address.IsIPv6SiteLocal ||
               addressBytes.Length > 0 && (addressBytes[0] & 0xfe) == 0xfc;
    }

    private sealed class FixedWebProxy : IWebProxy
    {
        private readonly Uri proxyUri;

        public FixedWebProxy(Uri proxyUri)
        {
            this.proxyUri = proxyUri;
        }

        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination)
        {
            return proxyUri;
        }

        public bool IsBypassed(Uri host)
        {
            return false;
        }
    }
}
