using System.Globalization;
using System.Xml.Linq;
using NetTopologySuite.Geometries;
using System.Linq;
using Wayfarer.Models;

namespace Wayfarer.Parsers;

/// <summary>
/// Builds a “Wayfarer-Extended-KML” document that preserves every Trip,
/// Region, Place, Area and Segment field so the file can be re-imported 1-for-1.
/// </summary>
public class TripWayfarerKmlExporter
{
    private const string KmlNs = "http://www.opengis.net/kml/2.2";
    private const string WfNs = "https://wayfarer.app/kml/2025/wayfarer";
    private static readonly XNamespace X = KmlNs;
    private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    public static string BuildKml(Trip trip)
    {
        /* 1) <Style> elements (icons) */
        var styles = (trip.Regions ?? Enumerable.Empty<Region>())
            .SelectMany(r => r.Places ?? Enumerable.Empty<Place>())
            .Select(p => new { p.IconName, p.MarkerColor })
            .Distinct()
            .Select(ic => new XElement(X + "Style",
                new XAttribute("id", $"wf_{ic.IconName}_{ic.MarkerColor}"),
                new XElement(X + "IconStyle",
                    new XElement(X + "Icon",
                        new XElement(X + "href",
                            $"/icons/wayfarer-map-icons/dist/png/marker/{ic.MarkerColor}/{ic.IconName}.png")))));


        /* 2) root <Document> */
        var doc = new XElement(X + "Document",
            new XElement(X + "name", trip.Name));

        // core metadata
        doc.Add(
            Ext("TripId", trip.Id),
            Ext("UpdatedAt", trip.UpdatedAt.ToString("O")),
            Ext("CoverImageUrl", trip.CoverImageUrl),
            Ext("NotesHtml", trip.Notes ?? string.Empty),
            Ext("CenterLat", trip.CenterLat),
            Ext("CenterLon", trip.CenterLon),
            Ext("Zoom", trip.Zoom));

        // tags - store as comma-separated slugs
        if (trip.Tags != null && trip.Tags.Any())
        {
            var tagSlugs = string.Join(",", trip.Tags.OrderBy(t => t.Name).Select(t => t.Slug));
            doc.Add(Ext("Tags", tagSlugs));
        }

        doc.Add(styles);


        /* 3) Regions → Places & Areas */
        foreach (var region in (trip.Regions ?? Enumerable.Empty<Region>())
                 .OrderBy(r => r.DisplayOrder))
        {
            var folder = new XElement(X + "Folder",
                new XElement(X + "name", region.Name));

            folder.Add(
                Ext("RegionId", region.Id),
                Ext("TripId", trip.Id),
                Ext("DisplayOrder", region.DisplayOrder),
                Ext("NotesHtml", region.Notes ?? string.Empty),
                Ext("CenterLat", region.Center?.Y),
                Ext("CenterLon", region.Center?.X)
            );

            // — Places —
            foreach (var place in (region.Places ?? Enumerable.Empty<Place>())
                     .OrderBy(p => p.DisplayOrder))
            {
                if (place.Location == null) continue;

                var placemark = new XElement(X + "Placemark",
                    new XElement(X + "name", place.Name),
                    new XElement(X + "styleUrl", $"#wf_{place.IconName}_{place.MarkerColor}"),
                    new XElement(X + "Point",
                        new XElement(X + "coordinates",
                            $"{place.Location.X.ToString(CI)},{place.Location.Y.ToString(CI)},0")));

                placemark.Add(
                    Ext("PlaceId", place.Id),
                    Ext("RegionId", region.Id),
                    Ext("DisplayOrder", place.DisplayOrder),
                    Ext("NotesHtml", place.Notes ?? string.Empty),
                    Ext("IconName", place.IconName),
                    Ext("MarkerColor", place.MarkerColor),
                    Ext("Address", place.Address ?? string.Empty)
                );

                folder.Add(placemark);
            }

            // — Areas —
            foreach (var area in (region.Areas ?? Enumerable.Empty<Area>())
                     .OrderBy(a => a.DisplayOrder))
            {
                if (area.Geometry is not Polygon poly) continue;

                // build coordinate string: lon,lat,0 pairs space-delimited
                var coordsText = string.Join(" ",
                    poly.Coordinates.Select(c =>
                        $"{c.X.ToString(CI)},{c.Y.ToString(CI)},0"));

                var areaPm = new XElement(X + "Placemark",
                    new XElement(X + "name", area.Name),
                    new XElement(X + "Polygon",
                        new XElement(X + "tessellate", 1),
                        new XElement(X + "outerBoundaryIs",
                            new XElement(X + "LinearRing",
                                new XElement(X + "coordinates", coordsText)
                            )
                        )
                    )
                );

                areaPm.Add(
                    Ext("AreaId", area.Id),
                    Ext("RegionId", region.Id),
                    Ext("DisplayOrder", area.DisplayOrder),
                    Ext("FillHex", area.FillHex),
                    Ext("NotesHtml", area.Notes ?? string.Empty)
                );

                folder.Add(areaPm);
            }

            doc.Add(folder);
        }


        /* 4) Segments */
        if (trip.Segments?.Any() == true)
        {
            var segFolder = new XElement(X + "Folder",
                new XElement(X + "name", "Segments"));

            foreach (var seg in trip.Segments.OrderBy(s => s.DisplayOrder))
            {
                if (seg.RouteGeometry is not LineString line) continue;

                var placemark = new XElement(X + "Placemark",
                    new XElement(X + "name", seg.Mode),
                    new XElement(X + "LineString",
                        new XElement(X + "tessellate", 1),
                        new XElement(X + "coordinates",
                            string.Join(" ",
                                line.Coordinates.Select(c =>
                                    $"{c.X.ToString(CI)},{c.Y.ToString(CI)},0")))));

                placemark.Add(
                    Ext("SegmentId", seg.Id),
                    Ext("TripId", trip.Id),
                    Ext("FromPlaceId", seg.FromPlaceId),
                    Ext("ToPlaceId", seg.ToPlaceId),
                    Ext("Mode", seg.Mode),
                    Ext("DistanceKm", seg.EstimatedDistanceKm),
                    Ext("DurationMin", seg.EstimatedDuration?.TotalMinutes),
                    Ext("DisplayOrder", seg.DisplayOrder),
                    Ext("NotesHtml", seg.Notes ?? string.Empty)
                );

                segFolder.Add(placemark);
            }

            doc.Add(segFolder);
        }


        /* 5) Wrap in <kml> and return */
        var kml = new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement(X + "kml",
                new XAttribute(XNamespace.Xmlns + "wf", WfNs),
                doc));

        return kml.ToString();
    }

    // helper: one <ExtendedData><Data name=…><value>…</value></Data></ExtendedData>
    private static XElement Ext(string name, object? val) =>
        new XElement(X + "ExtendedData",
            new XElement(X + "Data", new XAttribute("name", name),
                new XElement(X + "value", val?.ToString() ?? string.Empty)));
}