using System.Globalization;
using System.Xml.Linq;
using NetTopologySuite.Geometries;

namespace Wayfarer.Parsers;

/// <summary>Reads and writes standard KML boundaries for Wayfarer's 2D Polygon Areas without repairing rings.</summary>
internal static class KmlPolygonGeometry
{
    private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Writes the shell followed by each hole, retaining vertex order and round-trip double precision.</summary>
    internal static IEnumerable<XElement> WriteBoundaries(Polygon polygon)
    {
        yield return WriteRing("outerBoundaryIs", polygon.ExteriorRing);
        for (var index = 0; index < polygon.NumInteriorRings; index++)
            yield return WriteRing("innerBoundaryIs", polygon.GetInteriorRingN(index));
    }

    /// <summary>Rejects any missing/malformed boundary or invalid combined topology before returning detached geometry.</summary>
    internal static Polygon Parse(XElement placemark)
    {
        var source = RequiredElement(placemark, "Polygon");
        ValidateStructure(placemark, source);
        var shell = ReadRing(RequiredElement(source, "outerBoundaryIs"));
        var holes = source.Elements(Kml + "innerBoundaryIs").Select(ReadRing).ToArray();
        var polygon = new Polygon(shell, holes) { SRID = 4326 };
        if (!polygon.IsValid) throw new FormatException("Polygon topology is invalid.");
        return polygon;
    }

    /// <summary>Rejects misplaced standard Polygon structure so direct-child reads cannot silently omit a hole.</summary>
    private static void ValidateStructure(XElement placemark, XElement polygon)
    {
        foreach (var element in placemark.Descendants().Where(element => element.Name.Namespace == Kml))
        {
            var correctlyPlaced = element.Name.LocalName switch
            {
                "Polygon" => element == polygon,
                "outerBoundaryIs" or "innerBoundaryIs" => element.Parent == polygon,
                "LinearRing" => element.Parent?.Name == Kml + "outerBoundaryIs"
                    || element.Parent?.Name == Kml + "innerBoundaryIs",
                "coordinates" => element.Parent?.Name == Kml + "LinearRing",
                _ => true
            };
            if (!correctlyPlaced)
                throw new FormatException($"Misplaced Polygon {element.Name.LocalName}.");
        }
    }

    /// <summary>Creates one boundary from an existing ring; saved geometry is never closed, reordered or normalized.</summary>
    private static XElement WriteRing(string boundary, LineString ring) => new(Kml + boundary,
        new XElement(Kml + "LinearRing", new XElement(Kml + "coordinates", string.Join(' ',
            ring.Coordinates.Select(coordinate =>
                $"{coordinate.X.ToString("R", Invariant)},{coordinate.Y.ToString("R", Invariant)},0")))));

    /// <summary>Requires a complete, closed ring and leaves validity of its relationship to other rings to NTS.</summary>
    private static LinearRing ReadRing(XElement boundary)
    {
        var text = RequiredElement(RequiredElement(boundary, "LinearRing"), "coordinates").Value;
        var coordinates = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(ReadCoordinate).ToArray();
        if (coordinates.Length < 4 || !coordinates[0].Equals2D(coordinates[^1]))
            throw new FormatException("Every Polygon ring must contain at least four positions and be closed.");
        return new LinearRing(coordinates) { SRID = 4326 };
    }

    /// <summary>Reads finite geographic positions; optional numeric altitude is accepted but Areas remain 2D.</summary>
    private static Coordinate ReadCoordinate(string token)
    {
        var parts = token.Split(',');
        if (parts.Length is < 2 or > 3
            || !double.TryParse(parts[0], NumberStyles.Float, Invariant, out var longitude)
            || !double.TryParse(parts[1], NumberStyles.Float, Invariant, out var latitude)
            || !double.IsFinite(longitude) || !double.IsFinite(latitude)
            || longitude is < -180 or > 180 || latitude is < -90 or > 90)
            throw new FormatException("Polygon has an invalid coordinate.");
        if (parts.Length == 3 && (!double.TryParse(parts[2], NumberStyles.Float, Invariant, out var altitude)
            || !double.IsFinite(altitude)))
            throw new FormatException("Polygon has an invalid altitude.");
        return new Coordinate(longitude, latitude);
    }

    /// <summary>Rejects missing and duplicate structural elements instead of selecting a valid subset.</summary>
    private static XElement RequiredElement(XElement owner, string name)
    {
        var elements = owner.Elements(Kml + name).ToArray();
        return elements.Length == 1 ? elements[0] : throw new FormatException($"Polygon requires exactly one {name}.");
    }
}
