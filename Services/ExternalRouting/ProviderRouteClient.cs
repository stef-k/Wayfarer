using System.Net;
using System.Net.Http.Headers;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Dispatches explicit routing adapters while preserving the established proposal client seam.</summary>
public sealed class ProviderRouteClient(
    RoutingBoundedExecutor executor, RoutingAttemptCoordinator attempts,
    PersonalProviderContactGate personalContacts) : IProviderRouteClient
{
    /// <inheritdoc />
    public async Task<ProviderRouteResult> RouteAsync(
        ResolvedRoutingProviderExecution execution, IReadOnlyList<RouteCoordinate> anchors,
        Func<CancellationToken, Task<bool>> validateAuthority, CancellationToken cancellationToken)
    {
        if (execution.PersonalProviderUserId == null || execution.Credential == null
            || !GeoapifyRouteCost.TryParse(execution.Profile, out var mode))
            return ProviderRouteResult.Invalid("unsupported-provider-mode");
        if (anchors.Count is < 2 or > 25 || anchors.Any(anchor => !anchor.IsValid))
            return ProviderRouteResult.Invalid("routing-cost-invalid");
        int cost;
        try { cost = GeoapifyRouteCost.Calculate(mode, anchors.Count); }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
        { return ProviderRouteResult.Invalid("routing-cost-invalid"); }
        var request = GeoapifyRoutingAdapter.BuildRelativeRequest(execution.Profile, anchors, execution.Credential);
        var responseExecution = await executor.GetJsonAsync(new Uri("https://api.geoapify.com/"), request,
            execution.ResponseSizeLimitBytes, TimeSpan.FromSeconds(execution.TimeoutSeconds),
            cancellationToken, prepareAttempt: token => attempts.PrepareAsync(execution, validateAuthority, token,
                async admissionToken =>
                {
                    var admission = await personalContacts.AdmitAsync(execution.PersonalProviderUserId,
                        PersonalProviderCapability.Routing, PersonalProviderProduct.Routing, cost, admissionToken);
                    return admission.Succeeded ? null : admission.Category == PersonalProviderAdmissionCategory.Exhausted
                        ? "routing-credit-exhausted" : "provider-configuration-stale";
                }));
        if (!responseExecution.Succeeded) return ProviderRouteResult.Invalid(responseExecution.ErrorCode!);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(responseExecution.Json!) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await GeoapifyRoutingAdapter.ParseAsync(response, anchors, cancellationToken);
    }
}

/// <summary>Executes a route request for a resolved personal provider.</summary>
public interface IProviderRouteClient
{
    /// <summary>Contacts the provider only after final authority validation and admission.</summary>
    Task<ProviderRouteResult> RouteAsync(ResolvedRoutingProviderExecution execution,
        IReadOnlyList<RouteCoordinate> anchors, Func<CancellationToken, Task<bool>> validateAuthority,
        CancellationToken cancellationToken);
}

/// <summary>Represents one longitude/latitude pair without provider measurements.</summary>
public readonly record struct RouteCoordinate(double Longitude, double Latitude)
{
    /// <summary>Gets whether both ordinates are finite WGS84 values.</summary>
    public bool IsValid => double.IsFinite(Longitude) && double.IsFinite(Latitude)
        && Longitude is >= -180 and <= 180 && Latitude is >= -90 and <= 90;
}

/// <summary>Contains validated provider route geometry, metrics, and instructions.</summary>
public sealed record ProviderRouteResult(
    bool Succeeded, IReadOnlyList<RouteCoordinate> Geometry, IReadOnlyList<RouteCoordinate> Waypoints, string? ErrorCode,
    double? DistanceMetres = null, double? DurationSeconds = null,
    IReadOnlyList<RouteInstruction>? RouteInstructions = null,
    IReadOnlyList<int>? StructuralWaypointIndices = null)
{
    /// <summary>Gets normalized instructions or an empty list.</summary>
    public IReadOnlyList<RouteInstruction> Instructions => RouteInstructions ?? [];
    /// <summary>Creates a bounded invalid result without provider details.</summary>
    public static ProviderRouteResult Invalid(string code) => new(false, [], [], code);
}

/// <summary>Contains one bounded provider-neutral route instruction.</summary>
public sealed record RouteInstruction(
    string Text, string Type, int FromIndex, int ToIndex, double DistanceMetres, double DurationSeconds);
