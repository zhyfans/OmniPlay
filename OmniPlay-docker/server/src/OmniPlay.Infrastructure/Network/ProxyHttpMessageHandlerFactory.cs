using System.Net;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.Network;

public static class ProxyHttpMessageHandlerFactory
{
    private static readonly HashSet<string> SupportedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        Uri.UriSchemeHttp,
        Uri.UriSchemeHttps,
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
            TryCreateProxyUri(settings, out var proxyUri) &&
            proxyUri is not null)
        {
            var proxy = new WebProxy(proxyUri);
            if (!string.IsNullOrWhiteSpace(settings.Username) || !string.IsNullOrWhiteSpace(settings.Password))
            {
                proxy.Credentials = new NetworkCredential(settings.Username.Trim(), settings.Password.Trim());
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
            !TryCreateProxyUri(settings, out var proxyUri) ||
            proxyUri is null)
        {
            return "direct";
        }

        return string.Join(
            '|',
            "proxy",
            proxyUri.AbsoluteUri,
            settings.Username.Trim(),
            settings.Password.Trim(),
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

        proxyUri = uri;
        return true;
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
}
