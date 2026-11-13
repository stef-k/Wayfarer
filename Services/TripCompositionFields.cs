using System.Text.Json;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Models.Planning;
using Wayfarer.Util;
using NetTopologySuite.IO;

namespace Wayfarer.Services;

/// <summary>Fixed selected product groups with exact originals; no generic property path or provider assertions.</summary>
internal static class TripCompositionFieldValues
{
    internal static IEnumerable<(string Key, TripCompositionField Field)> Selected(TripCompositionFields? fields)
    {
        if (fields == null) yield break;
        foreach (var property in typeof(TripCompositionFields).GetProperties())
            if (property.GetValue(fields) is TripCompositionField field)
                yield return (JsonNamingPolicy.CamelCase.ConvertName(property.Name), field);
    }

    internal static object? Current(object target, string key) => (target, key) switch
    {
        (Trip t, "name") => t.Name,
        (Region r, "name") => r.Name,
        (Place p, "name") => p.Name,
        (Area a, "name") => a.Name,
        (Segment s, "title") => s.Title,
        (Trip t, "notes") => RichNotes.Normalize(t.Notes),
        (Region r, "notes") => RichNotes.Normalize(r.Notes),
        (Place p, "notes") => RichNotes.Normalize(p.Notes),
        (Area a, "notes") => RichNotes.Normalize(a.Notes),
        (Segment s, "notes") => RichNotes.Normalize(s.Notes),
        (Trip t, "cover") => t.CoverImageUrl,
        (Region r, "cover") => r.CoverImageUrl,
        (Place p, "iconName") => p.IconName,
        (Place p, "markerColor") => p.MarkerColor,
        (Area a, "fillHex") => a.FillHex,
        (Place p, "address") => new { p.Address, p.ResolvedFeatureName, p.ResolvedFeatureType, p.AddressEnrichmentProvider, p.AddressEnrichmentStorageMode, p.AddressEnrichedAt },
        (Place p, "location") => Pair(p.Location),
        (Region r, "center") => Pair(r.Center),
        (Area a, "geometry") => new GeoJsonWriter().Write(a.Geometry),
        (Segment s, "route") => Route(s),
        (IPlanningTarget t, "planning") => t.Planning,
        (Trip t, "tags") => t.Tags.OrderBy(t => t.Slug, StringComparer.Ordinal).Select(t => t.Slug).ToArray(),
        (Trip t, "viewport") => new { t.CenterLat, t.CenterLon, t.Zoom },
        _ => throw Invalid()
    };

    internal static object Route(Segment s) => new
    {
        s.FromPlaceId,
        s.ToPlaceId,
        s.Mode,
        s.TransportProfileId,
        waypoints = s.Waypoints.OrderBy(w => w.Position).Select(w => new { w.PlaceId, w.Position, w.RouteVertexIndex }),
        routeJson = s.RouteGeometry == null ? null : new GeoJsonWriter().Write(s.RouteGeometry),
        s.EstimatedDistanceKm,
        estimatedDurationTicks = s.EstimatedDuration?.Ticks,
        estimatedDurationSource = s.EstimatedDurationSource.ToString(),
        s.RouteInstructionsJson,
        s.RouteProvider,
        s.RouteProviderConfigurationId,
        s.RouteProviderConfigurationVersion,
        s.RouteTransportProfileId,
        s.RouteGeneratedAt,
        s.RouteAttribution,
        s.RouteStorageMode,
        s.RouteMappingMode
    };

    internal static JsonElement Element(object? value) => JsonSerializer.SerializeToElement(value, Wire);
    // Originals include explicit nulls, unlike capture's omission of stored optional absence.
    internal static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };
    internal static bool Equal(JsonElement expected, object? value) => expected.ValueKind != JsonValueKind.Undefined && JsonElement.DeepEquals(expected, Element(value));
    internal static double[]? Pair(NetTopologySuite.Geometries.Point? p) => p == null ? null : [p.X, p.Y];

    internal static JsonElement Desired(TripCompositionField field, object source, string key)
    {
        if (field.Operation == "useSource") return Element(Current(source, key));
        if (field.Operation == "clear")
        {
            if (key is "name" or "geometry" or "location" or "route") throw Invalid();
            return Element(null);
        }
        if (field.Operation != "edit" || field.Value.ValueKind == JsonValueKind.Undefined) throw Invalid();
        if (key is "name" or "title" or "notes" or "iconName" or "markerColor" or "fillHex" or "cover" or "geometry"
            && field.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw Invalid();
        return field.Value;
    }

    /// <summary>Uses the established scalar validation and rich Notes normalization; route/location/planning are staged by their owners.</summary>
    internal static void Apply(object target, string key, JsonElement value, IReadOnlySet<string> icons, IReadOnlySet<string> colors)
    {
        if (key == "notes")
        {
            var notes = PlaceConditionalRequest.Value("notes", value, true, icons, colors);
            switch (target)
            {
                case Trip t: t.Notes = notes; break;
                case Region r: r.Notes = notes; break;
                case Place p: p.Notes = notes; break;
                case Area a: a.Notes = notes; break;
                case Segment s: s.Notes = notes; break;
                default: throw Invalid();
            }
            return;
        }
        if (key == "name")
        {
            var name = Name(value.GetString(), target is Region);
            switch (target)
            {
                case Trip t: t.Name = name; break;
                case Region r: r.Name = name; break;
                case Place p: p.Name = name; break;
                case Area a: a.Name = name; break;
                default: throw Invalid();
            }
            return;
        }
        switch (target, key)
        {
            case (Segment s, "title"): s.Title = TripCreditCodec.Title(value.GetString()); break;
            case (Place p, "iconName"): p.IconName = value.ValueKind == JsonValueKind.Null ? null : PlaceConditionalRequest.Value(key, value, true, icons, colors); break;
            case (Place p, "markerColor"): p.MarkerColor = value.ValueKind == JsonValueKind.Null ? null : PlaceConditionalRequest.Value(key, value, true, icons, colors); break;
            case (Area a, "fillHex"):
                if (value.ValueKind == JsonValueKind.Null) { a.FillHex = null; break; }
                var errors = new Dictionary<string, string[]>(); EditorAreaRequestParser.ValidateFillHex(value.GetString(), errors);
                if (errors.Count != 0) throw Invalid(); a.FillHex = value.GetString()!.Trim().ToLowerInvariant(); break;
            case (Trip t, "cover"): t.CoverImageUrl = Cover(value); break;
            case (Region r, "cover"): r.CoverImageUrl = Cover(value); break;
            case (Region r, "center"): r.Center = Coordinate(value); break;
            case (Area a, "geometry"):
                var geometry = JsonDocument.Parse(value.GetString() ?? throw Invalid()).RootElement;
                if (!EditorAreaRequestParser.TryParseGeometryUpdate(Element(new { geometry }), out var parsed, out _)) throw Invalid();
                a.Geometry = parsed.Geometry; break;
            case (Trip t, "viewport"):
                if (value.ValueKind == JsonValueKind.Null) { t.CenterLat = null; t.CenterLon = null; t.Zoom = null; break; }
                Models.Dtos.PlaceStructuralRequest.Fields(value, "centerLat", "centerLon", "zoom");
                var lat = value.GetProperty("centerLat").Deserialize<double?>(); var lon = value.GetProperty("centerLon").Deserialize<double?>();
                if (lat.HasValue != lon.HasValue || lat is < -90 or > 90 || lon is < -180 or > 180) throw Invalid();
                var zoom = value.GetProperty("zoom").Deserialize<int?>(); if (zoom is < 0 or > 22) throw Invalid();
                t.CenterLat = lat; t.CenterLon = lon; t.Zoom = zoom; break;
            default: throw Invalid();
        }
    }

    internal static string Name(string? value, bool region)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 200 || value.Contains('\0')
            || region && string.Equals(value.Trim(), "Unassigned Places", StringComparison.OrdinalIgnoreCase)) throw Invalid();
        return value.Trim();
    }
    internal static NetTopologySuite.Geometries.Point? Coordinate(JsonElement value)
    {
        var p = TripCompositionSource.Point(value.Deserialize<double[]?>());
        if (p != null && (p.X is < -180 or > 180 || p.Y is < -90 or > 90)) throw Invalid();
        return p;
    }
    private static string? Cover(JsonElement value)
    {
        var text = value.GetString();
        if (text != null && (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)) throw Invalid();
        return text;
    }
    internal static TripMetadataMutationRejected Invalid() => new(400, "composition-field-invalid", "The selected field or value is unavailable for this object. Review the named field and its complete original.");
}
