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
    MobileRoutingService routing, MobileRoutingProfileDiscoveryService discovery) : MobileApiController(dbContext, logger, userAccessor)
{
    /// <summary>Returns the complete current provider-neutral profile catalog without provider contact.</summary>
    [HttpGet("profiles")]
    public async Task<IActionResult> Profiles(CancellationToken cancellationToken)
    {
        var (user, error) = await EnsureAuthenticatedUserAsync(cancellationToken);
        return error ?? Ok(await discovery.DiscoverAsync(user!.Id, cancellationToken));
    }

    /// <summary>Returns no-contact capability for one stable Wayfarer transport profile identity.</summary>
    [HttpGet("capability/{transportProfileId:guid}")]
    public async Task<IActionResult> Capability(Guid transportProfileId,
        [FromQuery] string? discoveryCatalogIdentity, CancellationToken cancellationToken)
    {
        var (user, error) = await EnsureAuthenticatedUserAsync(cancellationToken);
        if (error != null) return error;
        if (discoveryCatalogIdentity is not null && !DiscoveryCatalogIdentity.IsValid(discoveryCatalogIdentity))
            return BadRequest(new MobileRoutingCapability("invalid-request", transportProfileId,
                null, null, null, null, null, null, null));
        return Ok(await routing.CapabilityAsync(
            user!.Id, transportProfileId, discoveryCatalogIdentity, cancellationToken));
    }

    /// <summary>Generates one bounded provider-neutral route without mutating server domain state.</summary>
    [HttpPost("route")]
    public async Task<IActionResult> Route(MobileRouteRequest request, CancellationToken cancellationToken)
    {
        var (user, error) = await EnsureAuthenticatedUserAsync(cancellationToken);
        if (error != null) return error;
        if (request.AdditionalFields is { Count: > 0 } || request.Anchors.Count > 3
            || request.SelectedProfileAuthorityIdentity is not null
                && !SelectedProfileAuthorityIdentity.IsValid(request.SelectedProfileAuthorityIdentity))
            return BadRequest(MobileRouteResponse.Failure("invalid-request"));
        var points = new[] { request.Origin }.Concat(request.Anchors).Concat([request.Destination])
            .Select(item => new RouteCoordinate(item.Longitude, item.Latitude)).ToArray();
        var result = await routing.RouteAsync(user!.Id, request.TransportProfileId, points,
            request.SelectedProfileAuthorityIdentity, cancellationToken);
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
    /// <summary>Gets or sets the optional exact selected execution-authority fence.</summary>
    [JsonConverter(typeof(RoutingIdentityJsonConverter))]
    public string? SelectedProfileAuthorityIdentity { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalFields { get; set; }
}

/// <summary>Contains one WGS84 coordinate with no provider semantics.</summary>
public sealed record MobileRouteCoordinate(double Longitude, double Latitude);

/// <summary>Contains bounded provider-neutral route output and no secret/admin endpoint fields.</summary>
public sealed record MobileRouteResponse(bool Succeeded, string Outcome, IReadOnlyList<RouteCoordinate>? Geometry,
    double? DistanceMetres, double? DurationSeconds, IReadOnlyList<RouteInstruction>? Instructions,
    DateTimeOffset? GeneratedAt, string? Provider, Guid? ProviderConfigurationId, string? MappingIdentity,
    Guid? TransportProfileId, IReadOnlyList<RouteCoordinate>? MatchPoints,
    IReadOnlyList<MobileRouteAttribution>? Attribution, string? StorageMode,
    string? SelectedProfileAuthorityIdentity)
{
    public static MobileRouteResponse From(MobileRouteServiceResult value) => new(value.Succeeded, value.Outcome,
        value.Geometry, value.DistanceMetres, value.DurationSeconds, value.Instructions, value.GeneratedAt,
        value.Provider, value.ProviderConfigurationId, value.MappingIdentity, value.TransportProfileId,
        value.MatchPoints, value.Attribution, value.StorageMode, value.SelectedProfileAuthorityIdentity);
    public static MobileRouteResponse Failure(string outcome) => From(MobileRouteServiceResult.Failure(outcome));
}

/// <summary>Collapses a supplied non-string identity into the bounded invalid-request path.</summary>
public sealed class RoutingIdentityJsonConverter : JsonConverter<string?>
{
    /// <inheritdoc />
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();
        using var ignored = JsonDocument.ParseValue(ref reader);
        return "!invalid";
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
