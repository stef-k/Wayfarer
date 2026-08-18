using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Wayfarer.Services.ExternalRouting;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Verifies endpoint SSRF policy, DNS pinning, and response bounds without live traffic.</summary>
public sealed class RoutingBoundedExecutorTests
{
    [Theory]
    [InlineData("https://user:pass@example.com")]
    [InlineData("https://example.com/#fragment")]
    [InlineData("https://example.com/?key=secret")]
    [InlineData("ftp://example.com")]
    [InlineData("https://*.example.com")]
    public void Policy_RejectsUnsafeEndpointShapes(string value)
    {
        var decision = Policy().Validate(value, [IPAddress.Parse("8.8.8.8")]);

        Assert.False(decision.Allowed);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("192.0.2.1")]
    [InlineData("::1")]
    [InlineData("2001:db8::1")]
    public void PublicPolicy_RejectsEveryRestrictedResolvedAddress(string address)
    {
        var decision = Policy().Validate(new Uri("https://routing.example"), [IPAddress.Parse("8.8.8.8"), IPAddress.Parse(address)]);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void DeploymentAllowlist_PermitsExactSelfHostedHttpHostAndCidr()
    {
        var policy = Policy(new RoutingSelfHostedAllowlistEntry("osrm.internal", "10.20.0.0/16", true));

        var decision = policy.Validate(new Uri("http://osrm.internal"), [IPAddress.Parse("10.20.1.7")]);

        Assert.True(decision.Allowed);
        Assert.Equal(IPAddress.Parse("10.20.1.7"), decision.SelectedAddress);
    }

    [Fact]
    public async Task Executor_PinsValidatedAddressAndReturnsBoundedJson()
    {
        var address = IPAddress.Parse("8.8.8.8");
        var transport = new RecordingTransport(Response("{\"code\":\"Ok\"}"));
        var executor = new RoutingBoundedExecutor(new StubResolver(address), Policy(), transport);

        var result = await executor.GetJsonAsync(new Uri("https://routing.example"), "route/v1/x", 262144, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(address, transport.Address);
        Assert.Equal("routing.example", transport.Uri!.Host);
    }

    [Fact]
    public async Task Executor_RejectsOversizedStreamingBody()
    {
        var body = new string('x', 262145);
        var executor = new RoutingBoundedExecutor(new StubResolver(IPAddress.Parse("8.8.8.8")), Policy(), new RecordingTransport(Response(body)));

        var result = await executor.GetJsonAsync(new Uri("https://routing.example"), "route", 262144, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("provider-response-too-large", result.ErrorCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Executor_RetriesOnlyApprovedTransientStatusOnce(HttpStatusCode status)
    {
        var transport = new SequenceTransport(new HttpResponseMessage(status), Response("{}"));
        var executor = new RoutingBoundedExecutor(new StubResolver(IPAddress.Parse("8.8.8.8")), Policy(), transport);

        var result = await executor.GetJsonAsync(new Uri("https://routing.example"), "route", 262144, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, transport.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Executor_DoesNotRetryNonApprovedStatus(HttpStatusCode status)
    {
        var transport = new SequenceTransport(new HttpResponseMessage(status), Response("{}"));
        var executor = new RoutingBoundedExecutor(new StubResolver(IPAddress.Parse("8.8.8.8")), Policy(), transport);

        await executor.GetJsonAsync(new Uri("https://routing.example"), "route", 262144, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(1, transport.Requests);
    }

    private static RoutingEndpointPolicy Policy(params RoutingSelfHostedAllowlistEntry[] entries) =>
        new(Options.Create(new RoutingOutboundOptions { SelfHostedAllowlist = [.. entries] }));

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubResolver(params IPAddress[] addresses) : IRoutingDnsResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>(addresses);
    }

    private sealed class RecordingTransport(HttpResponseMessage response) : IRoutingPinnedTransport
    {
        public IPAddress? Address { get; private set; }
        public Uri? Uri { get; private set; }

        public Task<HttpResponseMessage> SendAsync(Uri requestUri, IPAddress selectedAddress, string? bearerCredential, CancellationToken cancellationToken)
        {
            (Uri, Address) = (requestUri, selectedAddress);
            return Task.FromResult(response);
        }
    }

    private sealed class SequenceTransport(params HttpResponseMessage[] responses) : IRoutingPinnedTransport
    {
        public int Requests { get; private set; }

        public Task<HttpResponseMessage> SendAsync(Uri requestUri, IPAddress selectedAddress, string? bearerCredential, CancellationToken cancellationToken) =>
            Task.FromResult(responses[Requests++]);
    }
}
