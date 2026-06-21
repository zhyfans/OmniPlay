using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.Network;
using Xunit;

namespace OmniPlay.Tests;

public sealed class ProxyHttpMessageHandlerFactoryTests : IDisposable
{
    private readonly string? originalContainerFlag = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
    private readonly string? originalHostNetwork = Environment.GetEnvironmentVariable("OMNIPLAY_DOCKER_HOST_NETWORK");
    private readonly string? originalHostGateway = Environment.GetEnvironmentVariable("OMNIPLAY_DOCKER_HOST_GATEWAY");

    [Fact]
    public void ResolveProxyUriCandidatesKeepsLoopbackWhenHostNetworkIsEnabled()
    {
        Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", "true");
        Environment.SetEnvironmentVariable("OMNIPLAY_DOCKER_HOST_NETWORK", "1");
        Environment.SetEnvironmentVariable("OMNIPLAY_DOCKER_HOST_GATEWAY", "172.24.0.1");

        var candidates = ProxyHttpMessageHandlerFactory.ResolveProxyUriCandidates(
            new ProxySettings(IsEnabled: true, Url: "http://localhost:20171"));

        Assert.Contains(candidates, uri => uri.Host == "localhost" && uri.Port == 20171);
    }

    [Fact]
    public void ResolveProxyUriCandidatesRewritesLoopbackInBridgeNetwork()
    {
        Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", "true");
        Environment.SetEnvironmentVariable("OMNIPLAY_DOCKER_HOST_NETWORK", "0");
        Environment.SetEnvironmentVariable("OMNIPLAY_DOCKER_HOST_GATEWAY", "172.24.0.1");

        var candidates = ProxyHttpMessageHandlerFactory.ResolveProxyUriCandidates(
            new ProxySettings(IsEnabled: true, Url: "http://localhost:20171"));

        Assert.Contains(candidates, uri => uri.Host == "172.24.0.1" && uri.Port == 20171);
        Assert.DoesNotContain(candidates, uri => uri.Host == "localhost");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", originalContainerFlag);
        Environment.SetEnvironmentVariable("OMNIPLAY_DOCKER_HOST_NETWORK", originalHostNetwork);
        Environment.SetEnvironmentVariable("OMNIPLAY_DOCKER_HOST_GATEWAY", originalHostGateway);
    }
}
