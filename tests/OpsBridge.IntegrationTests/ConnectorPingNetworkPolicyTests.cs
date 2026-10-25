using System.Net;
using OpsBridge.Api.Endpoints;

namespace OpsBridge.IntegrationTests;

public sealed class ConnectorPingNetworkPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void RejectsPrivateLocalAndMetadataAddresses(string literal) =>
        Assert.False(ConnectorPingNetworkPolicy.IsPublicAddress(IPAddress.Parse(literal)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void AcceptsGlobalUnicastAddresses(string literal) =>
        Assert.True(ConnectorPingNetworkPolicy.IsPublicAddress(IPAddress.Parse(literal)));

    [Fact]
    public void HttpHandlerDisablesRedirectsAndProxy()
    {
        using var handler = ConnectorPingNetworkPolicy.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.NotNull(handler.ConnectCallback);
    }
}
