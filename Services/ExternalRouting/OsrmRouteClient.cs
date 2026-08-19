using System.Net;
using System.Net.Http.Headers;
using Wayfarer.Models;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Combines credential protection, bounded execution, and the explicit OSRM adapter.</summary>
public sealed class OsrmRouteClient : IOsrmRouteClient
{
    private readonly RoutingBoundedExecutor _executor;
    private readonly RoutingProviderCredentialService _credentials;
    private readonly RoutingAttemptCoordinator _attempts;

    /// <summary>Initializes the server-only OSRM client.</summary>
    public OsrmRouteClient(
        RoutingBoundedExecutor executor, RoutingProviderCredentialService credentials, RoutingAttemptCoordinator attempts)
        => (_executor, _credentials, _attempts) = (executor, credentials, attempts);

    /// <inheritdoc />
    public async Task<OsrmRouteResult> RouteAsync(
        RoutingProviderConfiguration provider, string profile, IReadOnlyList<RouteCoordinate> anchors,
        Func<CancellationToken, Task<bool>> validateAuthority, CancellationToken cancellationToken)
    {
        var credential = _credentials.Read(provider);
        if (!credential.Succeeded) return OsrmRouteResult.Invalid(credential.ErrorCode!);
        var request = OsrmRoutingAdapter.BuildRelativeRequest(profile, anchors);
        var execution = await _executor.GetJsonAsync(new Uri(provider.BaseEndpoint!), request,
            provider.ResponseSizeLimitBytes, TimeSpan.FromSeconds(provider.GenerationTimeoutSeconds), cancellationToken,
            credential.Credential, prepareAttempt: token => _attempts.PrepareAsync(provider, validateAuthority, token));
        if (!execution.Succeeded) return OsrmRouteResult.Invalid(execution.ErrorCode!);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(execution.Json!)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await OsrmRoutingAdapter.ParseAsync(response, cancellationToken);
    }
}

/// <summary>Routes authoritative anchors through one explicit mapped OSRM profile.</summary>
public interface IOsrmRouteClient
{
    /// <summary>Returns only parsed validated provider geometry and snapped waypoints.</summary>
    Task<OsrmRouteResult> RouteAsync(
        RoutingProviderConfiguration provider, string profile, IReadOnlyList<RouteCoordinate> anchors,
        Func<CancellationToken, Task<bool>> validateAuthority, CancellationToken cancellationToken);
}
