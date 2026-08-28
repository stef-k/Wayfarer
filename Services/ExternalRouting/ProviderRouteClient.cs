using System.Net;
using System.Net.Http.Headers;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Dispatches explicit routing adapters while preserving the established proposal client seam.</summary>
public sealed class ProviderRouteClient(
    OsrmRouteClient osrm, RoutingBoundedExecutor executor, RoutingAttemptCoordinator attempts,
    PersonalProviderContactGate personalContacts) : IOsrmRouteClient
{
    /// <inheritdoc />
    public async Task<OsrmRouteResult> RouteAsync(
        ResolvedRoutingProviderExecution execution, IReadOnlyList<RouteCoordinate> anchors,
        Func<CancellationToken, Task<bool>> validateAuthority, CancellationToken cancellationToken)
    {
        if (execution.Provider.AdapterType != Models.RoutingAdapterType.Geoapify)
            return await osrm.RouteAsync(execution, anchors, validateAuthority, cancellationToken);
        if (execution.PersonalProviderUserId == null || execution.Credential == null
            || !GeoapifyRouteCost.TryParse(execution.Profile, out var mode))
            return OsrmRouteResult.Invalid("unsupported-transport-profile");
        if (anchors.Count is < 2 or > 25 || anchors.Any(anchor => !anchor.IsValid))
            return OsrmRouteResult.Invalid("routing-cost-invalid");
        int cost;
        try { cost = GeoapifyRouteCost.Calculate(mode, anchors.Count); }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
        { return OsrmRouteResult.Invalid("routing-cost-invalid"); }
        var request = GeoapifyRoutingAdapter.BuildRelativeRequest(execution.Profile, anchors, execution.Credential);
        var responseExecution = await executor.GetJsonAsync(new Uri("https://api.geoapify.com/"), request,
            execution.Provider.ResponseSizeLimitBytes, TimeSpan.FromSeconds(execution.Provider.GenerationTimeoutSeconds),
            cancellationToken, prepareAttempt: token => attempts.PrepareAsync(execution.Provider, validateAuthority, token,
                async admissionToken =>
                {
                    var admission = await personalContacts.AdmitAsync(execution.PersonalProviderUserId,
                        PersonalProviderCapability.Routing, PersonalProviderProduct.Routing, cost, admissionToken);
                    return admission.Succeeded ? null : admission.Category == PersonalProviderAdmissionCategory.Exhausted
                        ? "routing-credit-exhausted" : "provider-configuration-stale";
                }));
        if (!responseExecution.Succeeded) return OsrmRouteResult.Invalid(responseExecution.ErrorCode!);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(responseExecution.Json!) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await GeoapifyRoutingAdapter.ParseAsync(response, anchors, cancellationToken);
    }
}
