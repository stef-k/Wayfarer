using System.Text.Json;
using System.Security.Cryptography;
using Wayfarer.Models;
using Microsoft.AspNetCore.DataProtection;

namespace Wayfarer.Services;

/// <summary>Protects provider concurrency values for the same-origin Segment editor contract.</summary>
public sealed class SegmentAggregateTokenService
{
    private const int ContractVersion = 1;
    private readonly IDataProtector _protector;
    private readonly IDataProtector _routeProtector;

    /// <summary>Creates the dedicated Segment aggregate protector.</summary>
    public SegmentAggregateTokenService(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("Wayfarer.TripEditor.SegmentAggregate.v1");
        _routeProtector = provider.CreateProtector("Wayfarer.TripEditor.SegmentRoute.v1");
    }

    /// <summary>Issues original material route/dependency evidence independently of Notes and physical xmin.</summary>
    public string IssueRoute(string userId, Guid tripId, Segment segment) =>
        _routeProtector.Protect(JsonSerializer.Serialize(new RoutePayload(1, userId, tripId, segment.Id, RouteFingerprint(segment))));

    /// <summary>Validates the separate purpose, scope and material original without exchanging or refreshing evidence.</summary>
    public bool MatchesRoute(string? token, string userId, Guid tripId, Segment segment)
    {
        if (!TryReadRoute(token, userId, tripId, segment.Id, out var fingerprint)) return false;
        return fingerprint == RouteFingerprint(segment);
    }

    /// <summary>Checks original evidence scope before acquiring mutation locks.</summary>
    public bool TryReadRoute(string? token, string userId, Guid tripId, Guid segmentId, out string? fingerprint)
    {
        fingerprint = null;
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            var payload = JsonSerializer.Deserialize<RoutePayload>(_routeProtector.Unprotect(token));
            if (payload is not { Version: 1 } || payload.UserId != userId || payload.TripId != tripId
                || payload.SegmentId != segmentId || string.IsNullOrEmpty(payload.Fingerprint)) return false;
            fingerprint = payload.Fingerprint;
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException) { return false; }
    }

    /// <summary>Fixed route projection; callers load ordered children and each anchor's Region before issue/compare.</summary>
    public static string RouteFingerprint(Segment segment)
    {
        var projection = new
        {
            segment.Id, segment.UserId, segment.TripId, segment.DisplayOrder, segment.FromPlaceId, segment.ToPlaceId,
            From = Anchor(segment.FromPlace), To = Anchor(segment.ToPlace),
            Waypoints = segment.Waypoints.OrderBy(w => w.Position)
                .Select(w => new { w.SegmentId, w.Position, w.PlaceId, w.RouteVertexIndex, Anchor = Anchor(w.Place) }),
            Geometry = segment.RouteGeometry == null ? null : Convert.ToHexString(segment.RouteGeometry.AsBinary()),
            segment.Mode, segment.TransportProfileId, segment.EstimatedDistanceKm,
            DurationTicks = segment.EstimatedDuration?.Ticks, segment.EstimatedDurationSource,
            segment.RouteInstructionsJson, segment.RouteProvider, segment.RouteProviderConfigurationId,
            segment.RouteProviderConfigurationVersion, segment.RouteTransportProfileId, segment.RouteMappingMode,
            GeneratedAtTicks = segment.RouteGeneratedAt?.UtcTicks, segment.RouteAttribution, segment.RouteStorageMode
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(projection)));
    }

    /// <summary>Only anchor identity, owning scope and exact geometry affect the original route boundary.</summary>
    private static object? Anchor(Place? place) => place == null ? null : new
    {
        place.Id, place.RegionId, TripId = place.Region?.TripId,
        Location = place.Location == null ? null : Convert.ToHexString(place.Location.AsBinary())
    };

    private sealed record RoutePayload(int Version, string UserId, Guid TripId, Guid SegmentId, string Fingerprint);

    /// <summary>Issues a token bound to one user-owned Segment aggregate.</summary>
    public string Issue(string userId, Guid tripId, Guid segmentId, uint rowVersion) =>
        _protector.Protect(JsonSerializer.Serialize(new Payload(ContractVersion, userId, tripId, segmentId, rowVersion)));

    /// <summary>Validates scope and returns the protected provider row version.</summary>
    public bool TryRead(string? token, string userId, Guid tripId, Guid segmentId, out uint rowVersion)
    {
        rowVersion = 0;
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(token));
            if (payload is null || payload.Version != ContractVersion || payload.UserId != userId
                || payload.TripId != tripId || payload.SegmentId != segmentId) return false;
            rowVersion = payload.RowVersion;
            return true;
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return false;
        }
    }

    private sealed record Payload(int Version, string UserId, Guid TripId, Guid SegmentId, uint RowVersion);
}
