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

        try
        {
            using var httpClient = ProxyHttpMessageHandlerFactory.CreateHttpClient(settings, forceProxy: true);
            httpClient.Timeout = TimeSpan.FromSeconds(12);
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
                    proxyUrl,
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
                proxyUrl,
                TestTargetUri.ToString(),
                statusCode,
                message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProxyConnectionTestResult(
                false,
                proxyUrl,
                TestTargetUri.ToString(),
                null,
                "代理检测超时。");
        }
        catch (HttpRequestException ex)
        {
            return new ProxyConnectionTestResult(
                false,
                proxyUrl,
                TestTargetUri.ToString(),
                null,
                $"代理检测失败：{ex.Message}");
        }
    }
}
