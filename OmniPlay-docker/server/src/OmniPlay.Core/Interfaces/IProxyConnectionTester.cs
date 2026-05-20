using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface IProxyConnectionTester
{
    Task<ProxyConnectionTestResult> TestAsync(
        ProxySettings settings,
        CancellationToken cancellationToken = default);
}
