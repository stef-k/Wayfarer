using System.Text.Json;
using System.Text.Json.Serialization;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services;

/// <summary>One strict authored route representation, validated before profile discovery and reused during apply.</summary>
internal sealed record TripCompositionRouteInput(Guid? FromPlaceId, Guid? ToPlaceId,
    IReadOnlyList<SegmentWaypointProposal> Waypoints, string? RouteJson, string Mode,
    Guid? TransportProfileId, string DurationSource, double? ManualDurationMinutes)
{
    internal LineString? Geometry { get; private init; }
    private static readonly JsonSerializerOptions Strict = new(TripCompositionFieldValues.Wire)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        NumberHandling = JsonNumberHandling.Strict
    };

    /// <summary>Rejects unsupported route selections and malformed nested facts before any database discovery or staging.</summary>
    internal static IReadOnlyDictionary<TripCompositionItem, TripCompositionRouteInput> Validate(IReadOnlyList<TripCompositionItem> items)
    {
        var routes = new Dictionary<TripCompositionItem, TripCompositionRouteInput>(ReferenceEqualityComparer.Instance);
        foreach (var item in items)
        {
            if (item == null) throw TripCompositionFieldValues.Invalid();
            if (item.Anchors != null && (item.Kind != "segment" || item.Anchors.Any(a => a == null)))
                throw TripCompositionFieldValues.Invalid();
            if (item.Fields?.Route is { } field)
            {
                if (item.Kind != "segment" || item.Decision is not ("reuse" or "copy")
                    || field.Operation is not ("edit" or "clear" or "useSource")) throw TripCompositionFieldValues.Invalid();
                if (field.Operation != "edit")
                {
                    if (field.Value.ValueKind != JsonValueKind.Undefined) throw TripCompositionFieldValues.Invalid();
                }
                else
                {
                    if (field.Value.ValueKind != JsonValueKind.Object) throw TripCompositionFieldValues.Invalid();
                    var route = field.Value.Deserialize<TripCompositionRouteInput>(Strict) ?? throw TripCompositionFieldValues.Invalid();
                    if (route.Mode == null || route.DurationSource is not ("Manual" or "Automatic")
                        || route.Waypoints == null || route.Waypoints.Count > 128 || route.Waypoints.Any(w => w == null)
                        || route.ManualDurationMinutes is { } minutes && (!double.IsFinite(minutes) || minutes < 0)
                        || route.DurationSource == "Manual" && route.ManualDurationMinutes == null)
                        throw TripCompositionFieldValues.Invalid();
                    routes.Add(item, route with { Geometry = ReadGeometry(route.RouteJson) });
                }
            }
            if (item.Children != null)
                foreach (var pair in Validate(item.Children)) routes.Add(pair.Key, pair.Value);
        }
        return routes;
    }

    /// <summary>Uses the ordinary route coordinate parser while enforcing strict GeoJSON members and duplicates.</summary>
    private static LineString? ReadGeometry(string? json)
    {
        if (json == null) return null;
        using var document = JsonDocument.Parse(json);
        PlaceStructuralRequest.RejectDuplicateMembers(document.RootElement);
        PlaceStructuralRequest.Fields(document.RootElement, ["type", "coordinates"]);
        var errors = new Dictionary<string, string[]>();
        var route = EditorSegmentRequestParser.ReadRequiredNullableRoute(
            TripCompositionFieldValues.Element(new { route = document.RootElement }), errors);
        if (errors.Count != 0 || route == null || !route.IsValid) throw TripCompositionFieldValues.Invalid();
        return route;
    }
}
