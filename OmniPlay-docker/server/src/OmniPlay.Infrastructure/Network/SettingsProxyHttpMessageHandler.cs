using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.Network;

public sealed class SettingsProxyHttpMessageHandler : HttpMessageHandler
{
    private readonly IAppSettingsRepository settingsRepository;
    private readonly object syncRoot = new();
    private readonly Dictionary<string, HttpMessageInvoker> invokers = new(StringComparer.Ordinal);
    private bool disposed;

    public SettingsProxyHttpMessageHandler(IAppSettingsRepository settingsRepository)
    {
        this.settingsRepository = settingsRepository;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var settings = (await settingsRepository.GetAsync(cancellationToken)).Proxy;
        var key = ProxyHttpMessageHandlerFactory.BuildHandlerKey(settings, request.RequestUri);
        var invoker = GetOrCreateInvoker(key, settings, request.RequestUri);
        return await invoker.SendAsync(request, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (syncRoot)
            {
                if (!disposed)
                {
                    foreach (var invoker in invokers.Values)
                    {
                        invoker.Dispose();
                    }

                    invokers.Clear();
                    disposed = true;
                }
            }
        }

        base.Dispose(disposing);
    }

    private HttpMessageInvoker GetOrCreateInvoker(string key, ProxySettings settings, Uri? requestUri)
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (invokers.TryGetValue(key, out var invoker))
            {
                return invoker;
            }

            var handler = ProxyHttpMessageHandlerFactory.CreateHandler(settings, requestUri);
            invoker = new HttpMessageInvoker(handler, disposeHandler: true);
            invokers[key] = invoker;
            return invoker;
        }
    }
}
