using System.Net;
using System.Net.Sockets;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Executor_RetriesBoundedDnsConnectionFailureOnce(bool httpFailure)
    {
        var resolver = new ThrowingResolver(httpFailure
            ? new HttpRequestException("https://secret.invalid/37.9,23.7?key=credential")
            : new SocketException((int)SocketError.HostNotFound));
        var admissions = 0;
        var executor = new RoutingBoundedExecutor(resolver, Policy(), new SequenceTransport());

        var result = await executor.GetJsonAsync(new Uri("https://routing.example"), "route", 262144,
            TimeSpan.FromSeconds(5), CancellationToken.None, admitAttempt: () => { admissions++; return true; });

        Assert.Equal("provider-connection-failure", result.ErrorCode);
        Assert.Null(result.Json);
        Assert.Equal(2, resolver.Requests);
        Assert.Equal(2, admissions);
        Assert.DoesNotContain("credential", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Executor_DnsRetryStopsWhenSecondAttemptIsNotAdmitted()
    {
        var resolver = new ThrowingResolver(new SocketException((int)SocketError.HostNotFound));
        var admissions = 0;
        var executor = new RoutingBoundedExecutor(resolver, Policy(), new SequenceTransport());

        var result = await executor.GetJsonAsync(new Uri("https://routing.example"), "route", 262144,
            TimeSpan.FromSeconds(5), CancellationToken.None,
            admitAttempt: () => ++admissions == 1);

        Assert.Equal("provider-rate-limited", result.ErrorCode);
        Assert.Equal(1, resolver.Requests);
        Assert.Equal(2, admissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Executor_BoundsResponseStreamFailureWithoutRetry(bool httpFailure)
    {
        var transport = new SequenceTransport(StreamFailureResponse(httpFailure
            ? new HttpRequestException("response https://secret.invalid credential 37.9,23.7")
            : new IOException("response-content-secret")));
        var admissions = 0;
        var executor = new RoutingBoundedExecutor(new StubResolver(IPAddress.Parse("8.8.8.8")), Policy(), transport);

        var result = await executor.GetJsonAsync(new Uri("https://routing.example"), "route", 262144,
            TimeSpan.FromSeconds(5), CancellationToken.None, admitAttempt: () => { admissions++; return true; });

        Assert.Equal("provider-response-failure", result.ErrorCode);
        Assert.Null(result.Json);
        Assert.Equal(1, transport.Requests);
        Assert.Equal(1, admissions);
        Assert.DoesNotContain("secret", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Executor_PreservesCallerCancellationDuringDnsAndResponseBody(bool responseBody)
    {
        var blocker = new BlockingStage();
        IRoutingDnsResolver resolver = responseBody
            ? new StubResolver(IPAddress.Parse("8.8.8.8")) : new BlockingResolver(blocker);
        var transport = responseBody
            ? new SequenceTransport(BlockingResponse(blocker))
            : new SequenceTransport();
        var executor = new RoutingBoundedExecutor(resolver, Policy(), transport);
        using var cancellation = new CancellationTokenSource();
        var pending = executor.GetJsonAsync(new Uri("https://routing.example"), "route", 262144,
            TimeSpan.FromSeconds(5), cancellation.Token);
        await blocker.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        var result = await pending;

        Assert.Equal("request-cancelled", result.ErrorCode);
        Assert.Equal(responseBody ? 1 : 0, transport.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Executor_ClassifiesDeadlineDuringDnsAndResponseBody(bool responseBody)
    {
        var blocker = new BlockingStage();
        IRoutingDnsResolver resolver = responseBody
            ? new StubResolver(IPAddress.Parse("8.8.8.8")) : new BlockingResolver(blocker);
        var transport = responseBody
            ? new SequenceTransport(BlockingResponse(blocker))
            : new SequenceTransport();
        var executor = new RoutingBoundedExecutor(resolver, Policy(), transport);

        var result = await executor.GetJsonAsync(new Uri("https://routing.example"), "route", 262144,
            TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.Equal("provider-timeout", result.ErrorCode);
        Assert.Equal(responseBody ? 1 : 0, transport.Requests);
    }

    [Theory]
    [InlineData(CancellationStage.Dns, true)]
    [InlineData(CancellationStage.Send, true)]
    [InlineData(CancellationStage.ResponseBody, true)]
    [InlineData(CancellationStage.Dns, false)]
    [InlineData(CancellationStage.Send, false)]
    [InlineData(CancellationStage.ResponseBody, false)]
    public async Task Executor_ClassifiesWrappedCancellationWithoutRetryOrReadmission(
        CancellationStage stage, bool callerCancellation)
    {
        var blocker = new BlockingStage();
        var stream = new CancellationReadStream(blocker);
        var resolver = new CancellationResolver(stage, blocker);
        var transport = new CancellationTransport(stage, blocker, stream);
        var admissions = 0;
        var executor = new RoutingBoundedExecutor(resolver, Policy(), transport);
        using var cancellation = new CancellationTokenSource();
        var timeout = callerCancellation ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(50);
        var pending = executor.GetJsonAsync(new Uri("https://routing.example"), "route", 262144,
            timeout, cancellation.Token, admitAttempt: () => { admissions++; return true; });

        await blocker.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        if (callerCancellation) cancellation.Cancel();
        var result = await pending;

        Assert.Equal(callerCancellation ? "request-cancelled" : "provider-timeout", result.ErrorCode);
        Assert.NotEqual("provider-connection-failure", result.ErrorCode);
        Assert.NotEqual("provider-response-failure", result.ErrorCode);
        Assert.Null(result.Json);
        Assert.Equal(1, admissions);
        Assert.Equal(1, resolver.Requests);
        Assert.Equal(stage == CancellationStage.Dns ? 0 : 1, transport.Requests);
        Assert.Equal(stage == CancellationStage.ResponseBody, stream.Disposed);
        Assert.DoesNotContain("secret", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static RoutingEndpointPolicy Policy() => new();

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage StreamFailureResponse(Exception exception) => new(HttpStatusCode.OK)
    { Content = new StreamContent(new ThrowingReadStream(exception)) { Headers = { ContentType = new("application/json") } } };

    private static HttpResponseMessage BlockingResponse(BlockingStage blocker) => new(HttpStatusCode.OK)
    { Content = new StreamContent(new BlockingReadStream(blocker)) { Headers = { ContentType = new("application/json") } } };

    private sealed class StubResolver(params IPAddress[] addresses) : IRoutingDnsResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>(addresses);
    }

    private sealed class ThrowingResolver(Exception exception) : IRoutingDnsResolver
    {
        public int Requests { get; private set; }
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromException<IReadOnlyList<IPAddress>>(exception);
        }
    }

    private sealed class BlockingResolver(BlockingStage blocker) : IRoutingDnsResolver
    {
        public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            blocker.Signal();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }
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

    private sealed class BlockingStage
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task;
        public void Signal() => _entered.TrySetResult();
    }

    public enum CancellationStage { Dns, Send, ResponseBody }

    private sealed class CancellationResolver(CancellationStage stage, BlockingStage blocker) : IRoutingDnsResolver
    {
        public int Requests { get; private set; }

        public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Requests++;
            if (stage != CancellationStage.Dns) return [IPAddress.Parse("8.8.8.8")];
            blocker.Signal();
            await WaitAndThrowWrappedCancellationAsync(cancellationToken);
            return [];
        }
    }

    private sealed class CancellationTransport(
        CancellationStage stage, BlockingStage blocker, CancellationReadStream stream) : IRoutingPinnedTransport
    {
        public int Requests { get; private set; }

        public async Task<HttpResponseMessage> SendAsync(
            Uri requestUri, IPAddress selectedAddress, string? bearerCredential, CancellationToken cancellationToken)
        {
            Requests++;
            if (stage == CancellationStage.Send)
            {
                blocker.Signal();
                await WaitAndThrowWrappedCancellationAsync(cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream) { Headers = { ContentType = new("application/json") } }
            };
        }
    }

    private sealed class CancellationReadStream(BlockingStage blocker) : Stream
    {
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            blocker.Signal();
            await WaitAndThrowWrappedCancellationAsync(cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override ValueTask DisposeAsync() { Disposed = true; return base.DisposeAsync(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task WaitAndThrowWrappedCancellationAsync(CancellationToken cancellationToken)
    {
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
        catch (OperationCanceledException exception)
        {
            throw new HttpRequestException("secret cancellation detail", exception);
        }
    }

    private sealed class ThrowingReadStream(Exception exception) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw exception;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(exception);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingReadStream(BlockingStage blocker) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            blocker.Signal();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
