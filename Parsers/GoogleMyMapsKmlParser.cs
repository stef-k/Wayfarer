using System.Xml.Linq;
using System.Globalization;
using NetTopologySuite.Geometries;
using System.Text.RegularExpressions;
using Wayfarer.Models;

namespace Wayfarer.Parsers;

public class GoogleMyMapsKmlParser
{
    public static Trip Parse(Stream stream, string userId)
    {
        XNamespace k = "http://www.opengis.net/kml/2.2";
        var doc = XDocument.Load(stream);
        var root = doc.Root?.Element(k + "Document")!;

        var trip = new Trip
        {
            Id       = Guid.NewGuid(),
            Name     = root.Element(k + "name")?.Value ?? "Imported My Maps",
            UserId   = userId,
            Regions  = new List<Region>(),
            Segments = new List<Segment>(),
            UpdatedAt= DateTime.UtcNow
        };

        /* 1 ── iterate layers (Folders) */
        foreach (var f in root.Elements(k + "Folder"))
        {
            var reg = new Region
            {
                Id     = Guid.NewGuid(),
                TripId = trip.Id,
                UserId = userId,
                Name   = StripPrefix(f.Element(k + "name")?.Value),
                Places = new List<Place>()
            };

            foreach (var pm in f.Elements(k + "Placemark"))
            {
                if (pm.Element(k + "Point") != null)
                    reg.Places.Add(ParsePlace(pm, reg.Id, userId));

                else if (pm.Element(k + "LineString") != null)
                    trip.Segments!.Add(ParseLine(pm, trip.Id, userId));
            }

            trip.Regions!.Add(reg);
        }

        /* 2 ── segments outside layers */
        foreach (var pm in root.Elements(k + "Placemark")
                     .Where(p=>p.Element(k+"LineString")!=null))
        {
            trip.Segments!.Add(ParseLine(pm, trip.Id, userId));
        }

        /* 3 ── best-effort From/To link */
        LinkSegmentsToPlaces(trip);

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
            Id        = Guid.NewGuid(),
            RegionId  = regionId,
            UserId    = userId,
            Name      = pm.Element(k + "name")?.Value ?? "Place",
            Notes     = pm.Element(k + "description")?.Value,
            Location  = new Point(lon, lat) { SRID = 4326 }
        };
    }
    
    static Segment ParseLine(XElement pm, Guid tripId, string userId)
    {
        XNamespace k = "http://www.opengis.net/kml/2.2";
        var coords = pm.Element(k + "LineString")!
            .Element(k + "coordinates")!.Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(c =>
            {
                var p = c.Split(',');
                return new Coordinate(
                    double.Parse(p[0], CultureInfo.InvariantCulture),
                    double.Parse(p[1], CultureInfo.InvariantCulture));
            })
            .ToArray();

        return new Segment
        {
            Id            = Guid.NewGuid(),
            TripId        = tripId,
            UserId        = userId,
            Mode          = pm.Element(k + "name")?.Value ?? "drive",
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
            seg.ToPlaceId   = FindNearest(line.EndPoint,   allPlaces, maxDist);
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
}