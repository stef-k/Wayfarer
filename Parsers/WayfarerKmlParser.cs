using System.Globalization;
using System.Xml.Linq;
using NetTopologySuite.Geometries;
using Wayfarer.Models;

namespace Wayfarer.Parsers;

/// <summary>Lightweight reader for a Wayfarer-Extended-KML file.</summary>
public class WayfarerKmlParser
{
    private const string KmlNs = "http://www.opengis.net/kml/2.2";
    private static readonly XNamespace X = KmlNs;
    private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    public static Trip Parse(Stream xml)
    {
        var doc = XDocument.Load(xml, LoadOptions.SetLineInfo);
        var kml = doc.Root ?? throw new FormatException("Missing <kml> root");

        var trip = new Trip();
        var tripDoc = kml.Element(X + "Document")
                      ?? throw new FormatException("Missing <Document>");

        trip.Name = tripDoc.Element(X + "name")?.Value ?? "Imported trip";
        trip.CoverImageUrl = ReadString(tripDoc, "CoverImageUrl");
        trip.Id = ReadGuid(tripDoc, "TripId") ?? Guid.NewGuid();
        trip.Notes = ReadString(tripDoc, "NotesHtml");
        trip.CenterLat = ReadDouble(tripDoc, "CenterLat");
        trip.CenterLon = ReadDouble(tripDoc, "CenterLon");
        trip.Zoom = ReadInt(tripDoc, "Zoom");
        trip.IsPublic = false; // imports are private by default
        trip.UpdatedAt = DateTime.UtcNow;

        // Parse tags - stored as comma-separated slugs
        var tagsCsv = ReadString(tripDoc, "Tags");
        if (!string.IsNullOrWhiteSpace(tagsCsv))
        {
            trip.Tags = tagsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(slug => new Tag { Slug = slug, Name = slug })
                .ToList();
        }

        var regDict = new Dictionary<Guid, Region>();
        var placeDict = new Dictionary<Guid, Place>();

        /* ---------- Regions + Places ------------------------------------ */
        foreach (var folder in tripDoc.Elements(X + "Folder")
                     .Where(f => f.Element(X + "name")?.Value != "Segments"))
        {
            var region = new Region
            {
                Id = ReadGuid(folder, "RegionId") ?? Guid.NewGuid(),
                TripId = trip.Id,
                Name = folder.Element(X + "name")?.Value ?? "Region",
                DisplayOrder = ReadInt(folder, "DisplayOrder") ?? 0,
                Notes = ReadString(folder, "NotesHtml")
            };
            var lat = ReadDouble(folder, "CenterLat");
            var lon = ReadDouble(folder, "CenterLon");
            region.Center = (lat is null || lon is null)
                ? null
                : new Point(lon.Value, lat.Value) { SRID = 4326 };

            regDict[region.Id] = region;

            /* places inside this region */
            foreach (var pm in folder.Elements(X + "Placemark"))
            {
                var coords = pm.Element(X + "Point")
                    ?.Element(X + "coordinates")?.Value;
                if (string.IsNullOrWhiteSpace(coords)) continue;

                var (lonP, latP) = ParseLonLat(coords);

                var place = new Place
                {
                    Id = ReadGuid(pm, "PlaceId") ?? Guid.NewGuid(),
                    RegionId = region.Id,
                    Name = pm.Element(X + "name")?.Value ?? "Place",
                    DisplayOrder = ReadInt(pm, "DisplayOrder") ?? 0,
                    Notes = ReadString(pm, "NotesHtml"),
                    IconName = ReadString(pm, "IconName"),
                    MarkerColor = ReadString(pm, "MarkerColor"),
                    Address = ReadString(pm, "Address"),
                    Location = new Point(lonP, latP) { SRID = 4326 }
                };
                (region.Places ??= new List<Place>()).Add(place);
                place.Region = region;
                placeDict[place.Id] = place;
            }

            /* areas inside this region */
            foreach (var pm in folder.Elements(X + "Placemark"))
            {
                var poly = pm.Element(X + "Polygon");
                if (poly == null) continue;

                var coords = poly.Element(X + "outerBoundaryIs")
                    ?.Element(X + "LinearRing")
                    ?.Element(X + "coordinates")?.Value;

                if (string.IsNullOrWhiteSpace(coords)) continue;

                var ring = new LinearRing(coords
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s =>
                    {
                        var (lon, lat) = ParseLonLat(s);
                        return new Coordinate(lon, lat);
                    }).ToArray());

                var area = new Area
                {
                    Id = ReadGuid(pm, "AreaId") ?? Guid.NewGuid(),
                    RegionId = region.Id,
                    Name = pm.Element(X + "name")?.Value ?? "Area",
                    DisplayOrder = ReadInt(pm, "DisplayOrder") ?? 0,
                    FillHex = ReadString(pm, "FillHex"),
                    Notes = ReadString(pm, "NotesHtml"),
                    Geometry = new Polygon(ring) { SRID = 4326 }
                };

                (region.Areas ??= new List<Area>()).Add(area);
            }
        }

        /* ---------- Segments folder ------------------------------------- */
        var segFolder = tripDoc.Elements(X + "Folder")
            .FirstOrDefault(f => f.Element(X + "name")?.Value == "Segments");
        var segments = new List<Segment>();
        if (segFolder != null)
        {
            foreach (var pm in segFolder.Elements(X + "Placemark"))
            {
                var coordsTxt = pm.Element(X + "LineString")
                    ?.Element(X + "coordinates")?.Value;
                if (string.IsNullOrWhiteSpace(coordsTxt)) continue;

                var line = new LineString(
                        coordsTxt.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .Select(c =>
                            {
                                var (lon, lat) = ParseLonLat(c);
                                return new Coordinate(lon, lat);
                            }).ToArray())
                    { SRID = 4326 };

                segments.Add(new Segment
                {
                    Id = ReadGuid(pm, "SegmentId") ?? Guid.NewGuid(),
                    TripId = trip.Id,
                    FromPlaceId = ReadGuid(pm, "FromPlaceId"),
                    ToPlaceId = ReadGuid(pm, "ToPlaceId"),
                    Mode = ReadString(pm, "Mode") ?? string.Empty,
                    EstimatedDistanceKm = ReadDouble(pm, "DistanceKm"),
                    EstimatedDuration = ReadDouble(pm, "DurationMin") is { } m
                        ? TimeSpan.FromMinutes(m)
                        : null,
                    DisplayOrder = ReadInt(pm, "DisplayOrder") ?? 0,
                    Notes = ReadString(pm, "NotesHtml"),
                    RouteGeometry = line
                });
            }
        }

        /* attach collections */
        trip.Regions = regDict.Values.ToList();
        trip.Segments = segments;

        return trip;
    }

    /* ---------- helpers ------------------------------------------------- */
    static (double lon, double lat) ParseLonLat(string csv)
    {
        var parts = csv.Split(',');
        return (
            double.Parse(parts[0], CI),
            double.Parse(parts[1], CI)
        );
    }

    static Guid? ReadGuid(XElement el, string name) => Guid.TryParse(ReadString(el, name), out var g) ? g : (Guid?)null;
    static int? ReadInt(XElement el, string name) => int.TryParse(ReadString(el, name), out var v) ? v : (int?)null;

    static double? ReadDouble(XElement el, string name) =>
        double.TryParse(ReadString(el, name), NumberStyles.Any, CI, out var d) ? d : (double?)null;

    static string? ReadString(XElement el, string name) =>
        el.Elements(X + "ExtendedData")
            .Elements(X + "Data")
            .FirstOrDefault(d => (string?)d.Attribute("name") == name)
            ?.Element(X + "value")?.Value;
}