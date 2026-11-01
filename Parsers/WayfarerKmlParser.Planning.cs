using System.Text.Json;
using System.Xml.Linq;
using Wayfarer.Models.Planning;

namespace Wayfarer.Parsers;

public static partial class WayfarerKmlParser
{
    /// <summary>Older files may omit parent metadata; a supplied contradictory reference is never authoritative.</summary>
    private static void ValidateDeclaredParent(XElement owner, string field, Guid expected)
    {
        if (OptionalGuid(owner, field) is { } supplied && supplied != expected)
            throw new FormatException("Invalid native graph membership.");
    }

    /// <summary>Bounds the complete codec value before JSON materialization; no truncation is permitted.</summary>
    public const int MaximumPlanningCharacters = 262144;

    /// <summary>Rejects reserved payloads anywhere before a legacy or generic reader can discard them.</summary>
    private static void ValidatePlanningPlacement(XDocument source, XElement document, IReadOnlyList<string> versions)
    {
        var claims = source.Descendants().Where(e => e.Name.LocalName.Equals("Planning", StringComparison.OrdinalIgnoreCase)
            || ((string?)e.Attribute("name"))?.Trim().Equals("Planning", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        var v4 = versions.Count == 1 && versions[0].Trim() == "4";
        if (claims.Length > 0 && !v4) throw new FormatException("Private planning requires native KML version 4.");
        foreach (var claim in claims)
        {
            var owner = claim.Parent?.Parent;
            if (claim.Name != Kml + "Data" || (string?)claim.Attribute("name") != "Planning"
                || claim.Parent?.Name != Kml + "ExtendedData" || owner == null || NativeOwnerKind(owner, document) == null)
                throw new FormatException("Misplaced private planning metadata.");
        }
        if (v4)
        {
            if (source.Root?.Name != Kml + "kml") throw new FormatException("Invalid native KML root.");
            ValidateV4Structure(document);
        }
    }

    /// <summary>Recognizes only direct graph nodes, never a similarly named ignored nested folder.</summary>
    private static string? NativeOwnerKind(XElement owner, XElement document)
    {
        if (owner == document) return "trip";
        if (owner.Name == Kml + "Folder" && owner.Parent == document && Values(owner, "RegionId").Count == 1) return "region";
        if (owner.Name != Kml + "Placemark" || owner.Parent?.Name != Kml + "Folder" || owner.Parent.Parent != document) return null;
        if (owner.Parent.Element(Kml + "name")?.Value == "Segments" && Values(owner, "SegmentId").Count == 1) return "segment";
        if (Values(owner.Parent, "RegionId").Count != 1) return null;
        if (Values(owner, "AreaId").Count == 1 && owner.Element(Kml + "Polygon") != null) return "area";
        return Values(owner, "PlaceId").Count == 1 ? "place" : null;
    }

    /// <summary>V4's closed native metadata shape prevents duplicate, foreign-parent or silently ignored claims.</summary>
    private static void ValidateV4Structure(XElement document)
    {
        var fields = new Dictionary<string, string[]>
        {
            ["trip"] = ["WayfarerSchemaVersion", "TripId", "UpdatedAt", "CoverImageUrl", "NotesHtml", "CenterLat", "CenterLon", "Zoom", "Tags"],
            ["region"] = ["RegionId", "TripId", "DisplayOrder", "NotesHtml", "CenterLat", "CenterLon"],
            ["place"] = ["PlaceId", "RegionId", "DisplayOrder", "NotesHtml", "IconName", "MarkerColor", "Address", "ResolvedFeatureName", "ResolvedFeatureType", "AddressEnrichmentProvider", "AddressEnrichmentStorageMode", "AddressEnrichedAt"],
            ["area"] = ["AreaId", "RegionId", "DisplayOrder", "NotesHtml", "FillHex"],
            ["segment"] = ["SegmentId", "FromPlaceId", "ToPlaceId", "Mode", "TransportProfileKey", "DistanceKm", "DurationTicks", "DurationSource", "DisplayOrder", "NotesHtml", "HasCustomRoute", "WaypointPlaceIds", "WaypointRouteVertexIndices", "TripId", "RouteProvenance"]
        };
        HashSet<Guid> identities = [];
        foreach (var owner in document.DescendantsAndSelf().Where(e => e == document || e.Name == Kml + "Folder" || e.Name == Kml + "Placemark"))
        {
            var kind = NativeOwnerKind(owner, document);
            if (kind == null)
            {
                if (owner.Name == Kml + "Folder" && owner.Parent == document && owner.Element(Kml + "name")?.Value == "Segments"
                    && !owner.Elements(Kml + "ExtendedData").Any()) continue;
                throw new FormatException("Unsupported native graph structure.");
            }
            var data = DirectData(owner);
            if (data.Keys.Any(key => key != "Planning" && !fields[kind].Contains(key)))
                throw new FormatException("Unsupported native metadata field.");
            var idKey = char.ToUpperInvariant(kind[0]) + kind[1..] + "Id";
            if (!identities.Add(RequiredGuid(owner, idKey))) throw new FormatException("Ambiguous native identity.");
            if (owner.Elements(Kml + "name").Count() > 1 || owner.Elements().Count(e => e.Name == Kml + "Point"
                || e.Name == Kml + "Polygon" || e.Name == Kml + "LineString") > 1)
                throw new FormatException("Ambiguous native content.");
            foreach (var geometry in owner.Elements().Where(e => e.Name == Kml + "Point" || e.Name == Kml + "LineString"))
                if (geometry.Elements(Kml + "coordinates").Count() != 1 || geometry.Descendants(Kml + "coordinates").Count() != 1
                    || geometry.Element(Kml + "coordinates")!.HasElements)
                    throw new FormatException("Ambiguous native coordinates.");
            if (kind == "region" || kind == "segment")
            {
                if (RequiredGuid(owner, "TripId") != RequiredGuid(document, "TripId")) throw new FormatException("Invalid graph membership.");
            }
            else if (kind == "place" || kind == "area")
            {
                if (RequiredGuid(owner, "RegionId") != RequiredGuid(owner.Parent!, "RegionId")) throw new FormatException("Invalid graph membership.");
            }
        }
        foreach (var data in document.Descendants(Kml + "Data"))
            if (data.Parent?.Name != Kml + "ExtendedData" || data.Parent.Parent == null
                || NativeOwnerKind(data.Parent.Parent, document) == null || data.Elements(Kml + "value").Count() != 1
                || data.Element(Kml + "value")!.HasElements)
                throw new FormatException("Misplaced or ambiguous native metadata.");
    }

    /// <summary>Uploaded values never receive the historical stored-value zone exemption.</summary>
    private static PlanningAggregate? ParsePlanning(XElement owner, string kind)
    {
        if (!DirectData(owner).TryGetValue("Planning", out var json)) return null;
        if (json.Length > MaximumPlanningCharacters) throw new FormatException("Planning exceeds the supported limit.");
        try
        {
            using var syntax = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            RejectAmbiguousProperties(syntax.RootElement);
            var value = PlanningCodec.Read(json);
            PlanningValidation.Validate(value, kind);
            return value;
        }
        catch (Exception error) when (error is JsonException or PlanningValidationException)
        {
            throw new FormatException("The private planning payload is invalid or incompatible.");
        }
    }

    /// <summary>The shared Web codec is case-insensitive; spelling aliases must not become duplicate semantic keys.</summary>
    private static void RejectAmbiguousProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectAmbiguousProperties(item);
        if (value.ValueKind != JsonValueKind.Object) return;
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new JsonException();
            RejectAmbiguousProperties(property.Value);
        }
    }
}
