using System.Net;
using System.Net.Http.Headers;
using Wayfarer.Models;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Combines credential protection, bounded execution, and the explicit OSRM adapter.</summary>
public sealed class OsrmRouteClient : IOsrmRouteClient
{
    private readonly RoutingBoundedExecutor _executor;
    private readonly RoutingAttemptCoordinator _attempts;

    /// <summary>Initializes the server-only OSRM client.</summary>
    public OsrmRouteClient(
        RoutingBoundedExecutor executor, RoutingAttemptCoordinator attempts)
        => (_executor, _attempts) = (executor, attempts);

    /// <inheritdoc />
    public async Task<OsrmRouteResult> RouteAsync(
        ResolvedRoutingProviderExecution execution, IReadOnlyList<RouteCoordinate> anchors,
        Func<CancellationToken, Task<bool>> validateAuthority, CancellationToken cancellationToken)
    {
        var provider = execution.Provider;
        var request = OsrmRoutingAdapter.BuildRelativeRequest(execution.Profile, anchors);
        var responseExecution = await _executor.GetJsonAsync(new Uri(provider.BaseEndpoint!), request,
            provider.ResponseSizeLimitBytes, TimeSpan.FromSeconds(provider.GenerationTimeoutSeconds), cancellationToken,
            execution.Credential, prepareAttempt: token => _attempts.PrepareAsync(provider, validateAuthority, token));
        if (!responseExecution.Succeeded) return OsrmRouteResult.Invalid(responseExecution.ErrorCode!);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(responseExecution.Json!)
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
        ResolvedRoutingProviderExecution execution, IReadOnlyList<RouteCoordinate> anchors,
        Func<CancellationToken, Task<bool>> validateAuthority, CancellationToken cancellationToken);
}
