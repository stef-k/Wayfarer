using System.Xml.Linq;
using System.Globalization;
using NetTopologySuite.Geometries;
using System.Text.RegularExpressions;
using Wayfarer.Models;

namespace Wayfarer.Parsers;

public class GoogleMyMapsKmlParser
{
    private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    public static Trip Parse(Stream stream, string userId)
    {
        XNamespace k = "http://www.opengis.net/kml/2.2";
        var doc = XDocument.Load(stream);
        var root = doc.Root?.Element(k + "Document")!;

        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            Name = root.Element(k + "name")?.Value ?? "Imported My Maps",
            UserId = userId,
            Regions = new List<Region>(),
            Segments = new List<Segment>(),
            UpdatedAt = DateTime.UtcNow
        };

        // Parse tags from ExtendedData if present (from Wayfarer export)
        XNamespace wf = "https://wayfarer.stefk.me/kml";
        var tagsCsv = root.Element(k + "ExtendedData")?.Element(wf + "Tags")?.Value;
        if (!string.IsNullOrWhiteSpace(tagsCsv))
        {
            trip.Tags = tagsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(slug => new Tag { Slug = slug, Name = slug })
                .ToList();
        }

        /* 1 ── iterate layers (Folders) */
        foreach (var f in root.Elements(k + "Folder"))
        {
            var reg = new Region
            {
                Id = Guid.NewGuid(),
                TripId = trip.Id,
                UserId = userId,
                Name = StripPrefix(f.Element(k + "name")?.Value),
                Places = new List<Place>()
            };

            foreach (var pm in f.Elements(k + "Placemark"))
            {
                if (pm.Element(k + "Point") != null)
                {
                    reg.Places.Add(ParsePlace(pm, reg.Id, userId));
                }
                else if (pm.Element(k + "LineString") != null)
                {
                    trip.Segments!.Add(ParseLine(pm, trip.Id, userId));
                }
                else if (pm.Element(k + "Polygon") != null)
                {
                    reg.Areas ??= new List<Area>();
                    reg.Areas.Add(ParseArea(pm, reg.Id));
                }
            }

            trip.Regions!.Add(reg);
        }

        /* 2 ── segments outside layers */
        foreach (var pm in root.Elements(k + "Placemark")
                     .Where(p => p.Element(k + "LineString") != null))
        {
            trip.Segments!.Add(ParseLine(pm, trip.Id, userId));
        }

        /* 3 ── best-effort From/To link */
        LinkSegmentsToPlaces(trip);

        // ---------- infer center and zoom if unset -----------------------------
        var coords = trip.Regions
            .SelectMany(r => r.Places ?? Enumerable.Empty<Place>())
            .Where(p => p.Location != null)
            .Select(p => p.Location!)
            .ToList();

        if (coords.Count > 0)
        {
            trip.CenterLat = coords.Average(p => p.Y);
            trip.CenterLon = coords.Average(p => p.X);
            trip.Zoom = 5; // safe default for country-level zoom
        }
        
        return trip;
    }

    static string StripPrefix(string? raw) =>
        // removes “01 – ” prefix we add during export
        string.IsNullOrWhiteSpace(raw)
            ? "Unnamed layer"
            : Regex.Replace(raw, @"^\d+\s*[-–]\s*", "");

    static Place ParsePlace(XElement pm, Guid regionId, string userId)
    {
        XNamespace k = "http://www.opengis.net/kml/2.2";
        var coord = pm.Element(k + "Point")!
            .Element(k + "coordinates")!.Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries);
        double lon = double.Parse(coord[0], CultureInfo.InvariantCulture);
        double lat = double.Parse(coord[1], CultureInfo.InvariantCulture);

        return new Place
        {
            Id = Guid.NewGuid(),
            RegionId = regionId,
            UserId = userId,
            Name = pm.Element(k + "name")?.Value ?? "Place",
            Notes = pm.Element(k + "description")?.Value,
            Location = new Point(lon, lat) { SRID = 4326 }
        };
    }

    static Segment ParseLine(XElement pm, Guid tripId, string userId)
    {
        XNamespace k = "http://www.opengis.net/kml/2.2";
        var coords = pm.Element(k + "LineString")!
            .Element(k + "coordinates")!.Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s) && s.Contains(','))
            .Select(s =>
            {
                var p = s.Split(',');
                if (p.Length < 2)
                    throw new FormatException($"Invalid coordinate pair in LineString: '{s}'");

                return new Coordinate(
                    double.Parse(p[0], CI),
                    double.Parse(p[1], CI));
            })
            .ToArray();

        return new Segment
        {
            Id = Guid.NewGuid(),
            TripId = tripId,
            UserId = userId,
            Mode = pm.Element(k + "name")?.Value ?? "drive",
            RouteGeometry = new LineString(coords) { SRID = 4326 }
        };
    }

    /* best-effort: snap first/last segment node to nearest Place ≤200 m */
    static void LinkSegmentsToPlaces(Trip trip)
    {
        const double maxDist = 0.2; // ~ km because SRID 4326 distance is deg
        var allPlaces = trip.Regions.SelectMany(r => r.Places!).ToList();

        foreach (var seg in trip.Segments ?? Enumerable.Empty<Segment>())
        {
            if (seg.RouteGeometry is not LineString line) continue;

            seg.FromPlaceId = FindNearest(line.StartPoint, allPlaces, maxDist);
            seg.ToPlaceId = FindNearest(line.EndPoint, allPlaces, maxDist);
        }

        static Guid? FindNearest(Point p, IEnumerable<Place> places, double limitKm)
        {
            var nearest = places
                .Select(pl => new { pl.Id, Dist = p.Distance(pl.Location) * 111 }) // deg→km
                .OrderBy(x => x.Dist)
                .FirstOrDefault();

            return nearest != null && nearest.Dist <= limitKm ? nearest.Id : null;
        }
    }

    static Area ParseArea(XElement pm, Guid regionId)
    {
        XNamespace k = "http://www.opengis.net/kml/2.2";
        var coordsText = pm.Element(k + "Polygon")
            ?.Element(k + "outerBoundaryIs")
            ?.Element(k + "LinearRing")
            ?.Element(k + "coordinates")?.Value;

        if (string.IsNullOrWhiteSpace(coordsText))
            throw new FormatException("Polygon has no coordinates");

        var coords = coordsText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s) && s.Contains(','))
            .Select(s =>
            {
                var p = s.Split(',');
                if (p.Length < 2)
                    throw new FormatException($"Invalid coordinate pair: '{s}'");

                return new Coordinate(
                    double.Parse(p[0], CI),
                    double.Parse(p[1], CI));
            })
            .ToArray();

        return new Area
        {
            Id = Guid.NewGuid(),
            RegionId = regionId,
            Name = pm.Element(k + "name")?.Value ?? "Area",
            Notes = pm.Element(k + "description")?.Value,
            Geometry = new Polygon(new LinearRing(coords)) { SRID = 4326 }
        };
    }
}