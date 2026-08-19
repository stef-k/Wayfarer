using System.Net;
using System.Net.Sockets;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Executes routing JSON requests with DNS pinning and bounded response handling.</summary>
public sealed class RoutingBoundedExecutor
{
    private readonly IRoutingDnsResolver _resolver;
    private readonly RoutingEndpointPolicy _policy;
    private readonly IRoutingPinnedTransport _transport;

    /// <summary>Initializes the routing-specific outbound executor.</summary>
    public RoutingBoundedExecutor(IRoutingDnsResolver resolver, RoutingEndpointPolicy policy, IRoutingPinnedTransport transport)
        => (_resolver, _policy, _transport) = (resolver, policy, transport);

    /// <summary>Resolves, validates, pins, and streams one JSON response under the configured byte limit.</summary>
    public async Task<RoutingExecutionResult> GetJsonAsync(
        Uri endpoint, string relativeRequest, int responseLimitBytes, TimeSpan timeout, CancellationToken cancellationToken,
        string? bearerCredential = null, Func<bool>? admitAttempt = null,
        Func<CancellationToken, Task<RoutingAttemptAdmission>>? prepareAttempt = null)
    {
        if (responseLimitBytes is < 262144 or > 2097152 || timeout > TimeSpan.FromSeconds(30))
            return RoutingExecutionResult.Failure("routing-policy-invalid");
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var admission = prepareAttempt == null
                    ? RoutingAttemptAdmission.Legacy(admitAttempt?.Invoke() != false)
                    : await prepareAttempt(cancellationToken);
                if (!admission.Succeeded) return RoutingExecutionResult.Failure(admission.ErrorCode!);
                IReadOnlyList<IPAddress> addresses;
                Task<IReadOnlyList<IPAddress>>? resolution = null;
                try
                {
                    var startError = admission.StartAttempt(timeout, cancellationToken,
                        token => resolution = _resolver.ResolveAsync(endpoint.Host, token));
                    if (startError != null) return RoutingExecutionResult.Failure(startError);
                    addresses = await resolution!;
                }
                catch (HttpRequestException exception) when (exception.InnerException is OperationCanceledException)
                { throw exception.InnerException; }
                catch (Exception exception) when (exception is HttpRequestException or SocketException)
                {
                    if (attempt == 0) continue;
                    return RoutingExecutionResult.Failure("provider-connection-failure");
                }
                var decision = _policy.Validate(endpoint, addresses);
                if (!decision.Allowed) return RoutingExecutionResult.Failure("routing-endpoint-unsafe");
                var requestUri = new Uri(endpoint.ToString().TrimEnd('/') + "/" + relativeRequest.TrimStart('/'));
                HttpResponseMessage response;
                try { response = await _transport.SendAsync(requestUri, decision.SelectedAddress!, bearerCredential, admission.AttemptToken); }
                catch (HttpRequestException exception) when (exception.InnerException is OperationCanceledException)
                { throw exception.InnerException; }
                catch (Exception exception) when (exception is HttpRequestException or SocketException)
                {
                    if (attempt == 0) continue;
                    return RoutingExecutionResult.Failure("provider-connection-failure");
                }
                using (response)
                {
                    if (attempt == 0 && IsRetryable(response.StatusCode)) continue;
                    try { return await ReadResponseAsync(response, responseLimitBytes, admission.AttemptToken); }
                    catch (HttpRequestException exception) when (exception.InnerException is OperationCanceledException)
                    { throw exception.InnerException; }
                    catch (Exception exception) when (exception is HttpRequestException or IOException)
                    { return RoutingExecutionResult.Failure("provider-response-failure"); }
                }
            }
            return RoutingExecutionResult.Failure("provider-connection-failure");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return RoutingExecutionResult.Failure("provider-timeout"); }
        catch (OperationCanceledException) { return RoutingExecutionResult.Failure("request-cancelled"); }
    }

    private static async Task<RoutingExecutionResult> ReadResponseAsync(
        HttpResponseMessage response, int responseLimitBytes, CancellationToken cancellationToken)
    {
        if ((int)response.StatusCode is >= 300 and < 400) return RoutingExecutionResult.Failure("provider-redirect-rejected");
        if (!response.IsSuccessStatusCode) return RoutingExecutionResult.Failure(ClassifyStatus(response.StatusCode));
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not ("application/json" or "application/problem+json") && mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) != true)
            return RoutingExecutionResult.Failure("provider-content-type-invalid");
        if (response.Content.Headers.ContentLength > responseLimitBytes)
            return RoutingExecutionResult.Failure("provider-response-too-large");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new MemoryStream(Math.Min(responseLimitBytes, 65536));
        var buffer = new byte[16384];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (destination.Length + read > responseLimitBytes)
                return RoutingExecutionResult.Failure("provider-response-too-large");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return new RoutingExecutionResult(true, destination.ToArray(), null);
    }

    private static bool IsRetryable(HttpStatusCode status) => status is
        HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static string ClassifyStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests => "provider-rate-limited",
        HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout => "provider-unavailable",
        _ => "provider-http-failure"
    };
}

/// <summary>Owns per-attempt concurrency after pacing, authority validation, rate admission, and timestamp recording.</summary>
public sealed class RoutingAttemptAdmission : IDisposable
{
    private readonly IDisposable? _lease;
    private readonly RoutingProviderPacer.RoutingPacingTurn? _turn;
    private readonly Func<bool>? _admitRate;
    private IDisposable? _deadline;
    private bool _legacy;
    private RoutingAttemptAdmission(bool succeeded, string? errorCode, IDisposable? lease = null,
        RoutingProviderPacer.RoutingPacingTurn? turn = null, Func<bool>? admitRate = null)
        => (Succeeded, ErrorCode, _lease, _turn, _admitRate) = (succeeded, errorCode, lease, turn, admitRate);
    public bool Succeeded { get; }
    public string? ErrorCode { get; }
    public CancellationToken AttemptToken { get; private set; }
    /// <summary>Creates a bounded failed admission.</summary>
    public static RoutingAttemptAdmission Failure(string code) => new(false, code);
    internal static RoutingAttemptAdmission Prepared(IDisposable lease,
        RoutingProviderPacer.RoutingPacingTurn turn, Func<bool> admitRate) => new(true, null, lease, turn, admitRate);
    internal static RoutingAttemptAdmission Legacy(bool admitted) => admitted
        ? new(true, null) { _legacy = true } : Failure("provider-rate-limited");
    internal string? StartAttempt(TimeSpan timeout, CancellationToken cancellationToken, Action<CancellationToken> beginDns)
    {
        if (_legacy)
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            source.CancelAfter(timeout);
            _deadline = source;
            AttemptToken = source.Token;
            beginDns(source.Token);
            return null;
        }
        var error = _turn!.StartAttempt(timeout, cancellationToken, _admitRate!, token =>
        {
            AttemptToken = token;
            beginDns(token);
        }, out _deadline);
        return error;
    }
    /// <inheritdoc />
    public void Dispose()
    {
        _deadline?.Dispose();
        _turn?.Dispose();
        _lease?.Dispose();
    }
}

/// <summary>Contains bounded response bytes or a safe Wayfarer error category.</summary>
public sealed record RoutingExecutionResult(bool Succeeded, byte[]? Json, string? ErrorCode)
{
    /// <summary>Creates a failure without raw provider details.</summary>
    public static RoutingExecutionResult Failure(string code) => new(false, null, code);
}

/// <summary>Resolves every A and AAAA address immediately before a routing connection.</summary>
public interface IRoutingDnsResolver
{
    /// <summary>Resolves the original configured host.</summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>Uses the system resolver without caching beyond the current request.</summary>
public sealed class RoutingDnsResolver : IRoutingDnsResolver
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>Sends a request over the selected address while retaining the original URI host.</summary>
public interface IRoutingPinnedTransport
{
    /// <summary>Sends one non-redirecting request pinned to the validated address.</summary>
    Task<HttpResponseMessage> SendAsync(Uri requestUri, IPAddress selectedAddress, string? bearerCredential, CancellationToken cancellationToken);
}

/// <summary>Creates a one-request handler whose connection callback pins the validated address.</summary>
public sealed class RoutingPinnedTransport : IRoutingPinnedTransport, IDisposable
{
    private static readonly HttpRequestOptionsKey<IPAddress> AddressKey = new("WayfarerRoutingPinnedAddress");
    private readonly SocketsHttpHandler _handler;
    private readonly HttpClient _client;

    /// <summary>Initializes one reusable non-redirecting transport with per-request address pinning.</summary>
    public RoutingPinnedTransport()
    {
        _handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.Zero,
            PooledConnectionIdleTimeout = TimeSpan.Zero,
            ConnectCallback = ConnectPinnedAsync
        };
        _client = new HttpClient(_handler, disposeHandler: false);
    }

    /// <inheritdoc />
    public async Task<HttpResponseMessage> SendAsync(Uri requestUri, IPAddress selectedAddress, string? bearerCredential, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Options.Set(AddressKey, selectedAddress);
        if (!string.IsNullOrEmpty(bearerCredential))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerCredential);
        return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
    }

    private static async ValueTask<Stream> ConnectPinnedAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(AddressKey, out var selectedAddress))
            throw new HttpRequestException("The routing connection has no validated address.");
        var socket = new Socket(selectedAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(selectedAddress, context.DnsEndPoint.Port), token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
}
