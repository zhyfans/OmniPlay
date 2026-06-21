using System.Net;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.Network;

public sealed class ProxyConnectionTester : IProxyConnectionTester
{
    private static readonly Uri TestTargetUri = new("https://api.themoviedb.org/3/configuration");

    public async Task<ProxyConnectionTestResult> TestAsync(
        ProxySettings settings,
        CancellationToken cancellationToken = default)
    {
        var proxyUrl = ProxyHttpMessageHandlerFactory.DisplayProxyUrl(settings);
        if (!settings.IsEnabled)
        {
            return new ProxyConnectionTestResult(false, proxyUrl, TestTargetUri.ToString(), null, "代理未启用。");
        }

        if (!ProxyHttpMessageHandlerFactory.TryCreateProxyUri(settings, out _))
        {
            return new ProxyConnectionTestResult(false, proxyUrl, TestTargetUri.ToString(), null, "代理地址无效。");
        }

        var errors = new List<string>();
        foreach (var candidate in ProxyHttpMessageHandlerFactory.ResolveProxyUriCandidates(settings))
        {
            var candidateSettings = settings with { Url = candidate.AbsoluteUri };
            try
            {
                using var httpClient = ProxyHttpMessageHandlerFactory.CreateHttpClient(candidateSettings, forceProxy: true);
                httpClient.Timeout = TimeSpan.FromSeconds(10);
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("OmniPlay.NAS/0.1");
                using var request = new HttpRequestMessage(HttpMethod.Get, TestTargetUri);
                using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                var statusCode = (int)response.StatusCode;
                if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                {
                    return new ProxyConnectionTestResult(
                        false,
                        ProxyHttpMessageHandlerFactory.DisplayProxyUrl(candidateSettings),
                        TestTargetUri.ToString(),
                        statusCode,
                        "代理认证失败。");
                }

                var isReachable = statusCode < 500;
                var message = response.StatusCode == HttpStatusCode.Unauthorized
                    ? "代理连通正常，TMDB 未带 API Key 返回 401 属于正常现象。"
                    : isReachable
                        ? $"代理可用，目标返回 HTTP {statusCode}。"
                        : $"代理已连接，但目标返回 HTTP {statusCode}。";
                return new ProxyConnectionTestResult(
                    isReachable,
                    ProxyHttpMessageHandlerFactory.DisplayProxyUrl(candidateSettings),
                    TestTargetUri.ToString(),
                    statusCode,
                    message);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add($"{ProxyHttpMessageHandlerFactory.DisplayProxyUrl(candidateSettings)} 超时");
            }
            catch (HttpRequestException ex)
            {
                errors.Add($"{ProxyHttpMessageHandlerFactory.DisplayProxyUrl(candidateSettings)} {ex.Message}");
            }
        }

        var hint = IsLoopbackProxyUrl(settings.Url)
            ? "。如果使用 bridge 网络，Docker 容器内的 127.0.0.1/localhost 不是宿主机；请使用 host 网络，或改填宿主机 LAN IP 并确认代理监听 0.0.0.0/LAN。"
            : string.Empty;
        var messageText = errors.Count > 0
            ? $"代理检测失败：{string.Join("；", errors)}{hint}"
            : $"代理检测失败。{hint}";
        return new ProxyConnectionTestResult(false, proxyUrl, TestTargetUri.ToString(), null, messageText);
    }

    private static bool IsLoopbackProxyUrl(string value)
    {
        var candidate = value.Contains("://", StringComparison.Ordinal) ? value : $"http://{value}";
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
               && (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                   || IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
    }
}
