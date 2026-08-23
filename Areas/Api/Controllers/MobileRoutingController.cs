using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Services.ExternalRouting;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>Exposes authenticated provider-neutral mobile routing without provider selection or persistence.</summary>
[Route("api/mobile/routing")]
public sealed class MobileRoutingController(
    ApplicationDbContext dbContext, ILogger<BaseApiController> logger, IMobileCurrentUserAccessor userAccessor,
    MobileRoutingService routing) : MobileApiController(dbContext, logger, userAccessor)
{
    /// <summary>Returns no-contact capability for one stable Wayfarer transport profile identity.</summary>
    [HttpGet("capability/{transportProfileId:guid}")]
    public async Task<IActionResult> Capability(Guid transportProfileId, CancellationToken cancellationToken)
    {
        var (user, error) = await EnsureAuthenticatedUserAsync(cancellationToken);
        return error ?? Ok(await routing.CapabilityAsync(user!.Id, transportProfileId, cancellationToken));
    }

    /// <summary>Generates one bounded provider-neutral route without mutating server domain state.</summary>
    [HttpPost("route")]
    public async Task<IActionResult> Route(MobileRouteRequest request, CancellationToken cancellationToken)
    {
        var (user, error) = await EnsureAuthenticatedUserAsync(cancellationToken);
        if (error != null) return error;
        if (request.AdditionalFields is { Count: > 0 } || request.Anchors.Count > 3)
            return BadRequest(MobileRouteResponse.Failure("invalid-request"));
        var points = new[] { request.Origin }.Concat(request.Anchors).Concat([request.Destination])
            .Select(item => new RouteCoordinate(item.Longitude, item.Latitude)).ToArray();
        var result = await routing.RouteAsync(user!.Id, request.TransportProfileId, points, cancellationToken);
        return Ok(MobileRouteResponse.From(result));
    }
}

/// <summary>Contains only server-resolved mobile route inputs.</summary>
public sealed class MobileRouteRequest
{
    public Guid TransportProfileId { get; set; }
    public required MobileRouteCoordinate Origin { get; set; }
    public required MobileRouteCoordinate Destination { get; set; }
    public IReadOnlyList<MobileRouteCoordinate> Anchors { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalFields { get; set; }
}

/// <summary>Contains one WGS84 coordinate with no provider semantics.</summary>
public sealed record MobileRouteCoordinate(double Longitude, double Latitude);

/// <summary>Contains bounded provider-neutral route output and no secret/admin endpoint fields.</summary>
public sealed record MobileRouteResponse(bool Succeeded, string Outcome, IReadOnlyList<RouteCoordinate>? Geometry,
    double? DistanceMetres, double? DurationSeconds, IReadOnlyList<RouteInstruction>? Instructions,
    DateTimeOffset? GeneratedAt, string? Provider, Guid? ProviderConfigurationId, string? MappingIdentity,
    Guid? TransportProfileId, IReadOnlyList<RouteCoordinate>? MatchPoints,
    IReadOnlyList<MobileRouteAttribution>? Attribution, string? StorageMode)
{
    public static MobileRouteResponse From(MobileRouteServiceResult value) => new(value.Succeeded, value.Outcome,
        value.Geometry, value.DistanceMetres, value.DurationSeconds, value.Instructions, value.GeneratedAt,
        value.Provider, value.ProviderConfigurationId, value.MappingIdentity, value.TransportProfileId,
        value.MatchPoints, value.Attribution, value.StorageMode);
    public static MobileRouteResponse Failure(string outcome) => From(MobileRouteServiceResult.Failure(outcome));
}
