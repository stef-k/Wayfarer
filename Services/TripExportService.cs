using System.Globalization;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Services;
using static Wayfarer.Parsers.KmlMappings;

namespace Wayfarer.Parsers
{
    /// <summary>Generates PDF and KML exports for a Trip.</summary>
    public partial class TripExportService : ITripExportService
    {
        readonly ApplicationDbContext _db;
        readonly MapSnapshotService _snap;
        readonly IHttpContextAccessor _ctx;
        readonly LinkGenerator _link;
        readonly IRazorViewRenderer _razor;
        readonly ILogger<TripExportService> _logger;
        readonly IConfiguration _configuration;
        readonly SseService _sseService;
        readonly IImageProxyService _imageProxyService;
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        public TripExportService(
            ApplicationDbContext dbContext,
            MapSnapshotService mapSnapshot,
            IHttpContextAccessor httpContextAccessor,
            LinkGenerator linkGenerator,
            IRazorViewRenderer razor,
            ILogger<TripExportService> logger,
            IConfiguration configuration,
            SseService sseService,
            IImageProxyService imageProxyService)
        {
            _db = dbContext;
            _snap = mapSnapshot;
            _ctx = httpContextAccessor;
            _link = linkGenerator;
            _razor = razor;
            _logger = logger;
            _configuration = configuration;
            _sseService = sseService;
            _imageProxyService = imageProxyService;
        }

        /* ---------------------------------------------------------------- KML stubs */

        public string GenerateWayfarerKml(Guid tripId)
        {
            var trip = _db.Trips
                           .Include(t => t.Regions).ThenInclude(r => r.Places)
                           .Include(t => t.Regions).ThenInclude(r => r.Areas)
                           .Include(t => t.Segments).ThenInclude(s => s.FromPlace).ThenInclude(p => p!.Region).Include(t => t.Segments).ThenInclude(s => s.ToPlace).ThenInclude(p => p!.Region).Include(t => t.Segments).ThenInclude(s => s.Waypoints.OrderBy(w => w.Position)).ThenInclude(w => w.Place).ThenInclude(p => p.Region).Include(t => t.Segments).ThenInclude(s => s.TransportProfile)
                           .Include(t => t.Tags)
                           .AsNoTrackingWithIdentityResolution()
                           .FirstOrDefault(t => t.Id == tripId)
                       ?? throw new ArgumentException($"Trip not found: {tripId}", nameof(tripId));

            return TripWayfarerKmlExporter.BuildKml(trip);
        }

        /// <summary>
        /// My Maps exporter 
        /// </summary>
        /// <param name="tripId"></param>
        /// <returns></returns>
        public string GenerateGoogleMyMapsKml(Guid tripId)
        {
            var trip = _db.Trips
                .Include(t => t.Regions).ThenInclude(r => r.Places)
                .Include(t => t.Regions).ThenInclude(r => r.Areas)
                .Include(t => t.Segments)
                .Include(t => t.Tags)
                .AsNoTracking()
                .First(t => t.Id == tripId);

            /* namespaces --------------------------------------------------------- */
            XNamespace k = "http://www.opengis.net/kml/2.2";
            XNamespace wf = "https://wayfarer.stefk.me/kml"; // private → never shown

            /* root                                                                */
            var doc = new XElement(k + "Document",
                new XElement(k + "name", trip.Name));

            // Add tags as ExtendedData at document level
            if (trip.Tags != null && trip.Tags.Any())
            {
                var tagSlugs = string.Join(",", trip.Tags.OrderBy(t => t.Name).Select(t => t.Slug));
                doc.Add(new XElement(k + "ExtendedData",
                    new XElement(wf + "Tags", tagSlugs)));
            }

            /* 1 ── basic icon + line styles ------------------------------------- */
            var iconStyles = (trip.Regions ?? Enumerable.Empty<Region>())
                .SelectMany(r => r.Places ?? Enumerable.Empty<Place>())
                .Select(p => (p.IconName, p.MarkerColor))
                .Distinct()
                .Select(ic =>
                {
                    IconMapping.TryGetValue(ic.IconName ?? string.Empty, out var shape);
                    shape ??= "placemark_circle";

                    ColorMapping.TryGetValue(ic.MarkerColor ?? string.Empty, out var clr);
                    clr ??= "ff000000";

                    var href = $"http://maps.google.com/mapfiles/kml/shapes/{shape}.png";
                    return new XElement(k + "Style",
                        new XAttribute("id", $"wf_{ic.IconName}_{ic.MarkerColor}"),
                        new XElement(k + "IconStyle",
                            new XElement(k + "color", clr),
                            new XElement(k + "scale", 1.2),
                            new XElement(k + "Icon",
                                new XElement(k + "href", href)
                            )
                        )
                    );
                });

            // one shared line-style
            var lineStyle = new XElement(k + "Style",
                new XAttribute("id", "wf-line"),
                new XElement(k + "LineStyle",
                    new XElement(k + "color", "ff0000ff"),
                    new XElement(k + "width", 4)));

            var polyStyle = new XElement(k + "Style",
                new XAttribute("id", "wf-area"),
                new XElement(k + "PolyStyle",
                    new XElement(k + "color", "7dff6600") // semi-transparent orange
                )
            );
            doc.Add(polyStyle);
            doc.Add(iconStyles);
            doc.Add(lineStyle);

            /* 2 ── Regions → Folders ------------------------------------------- */
            foreach (var (reg, idx) in (trip.Regions ?? Enumerable.Empty<Region>())
                         .OrderBy(r => r.DisplayOrder)
                         .Select((r, i) => (r, i)))
            {
                var folder = new XElement(k + "Folder",
                    new XElement(k + "name", $"{idx + 1:00} – {reg.Name}"));

                /* 2a ── Places --------------------------------------------------- */
                foreach (var p in (reg.Places ?? Enumerable.Empty<Place>()).OrderBy(p => p.DisplayOrder))
                {
                    if (p.Location == null) continue;

                    folder.Add(
                        new XElement(k + "Placemark",
                            new XElement(k + "name", p.Name),
                            new XElement(k + "styleUrl", "#wf-icon"),
                            /* description (wrapped in CDATA) */
                            string.IsNullOrWhiteSpace(p.Notes)
                                ? null
                                : new XElement(k + "description", new XCData(Wayfarer.Util.RichNotes.Normalize(p.Notes)!)),
                            /* hidden place-id */
                            new XElement(k + "ExtendedData",
                                new XElement(wf + "PlaceId", p.Id)),
                            /* geometry */
                            new XElement(k + "Point",
                                new XElement(k + "coordinates",
                                    $"{p.Location.X},{p.Location.Y},0")))
                    );
                }


                // 2b ── Areas as Polygons -----------------------------------------
                foreach (var a in reg.Areas?.OrderBy(a => a.DisplayOrder) ?? Enumerable.Empty<Area>())
                {
                    if (a.Geometry is not Polygon poly) continue;

                    var coords = poly.Coordinates.ToList();
                    if (!coords.First().Equals2D(coords.Last()))
                        coords.Add(coords.First());

                    var coordsText = string.Join(" ",
                        coords.Select(c =>
                            $"{c.X.ToString(CI)},{c.Y.ToString(CI)},0"));

                    var placemark = new XElement(k + "Placemark",
                        new XElement(k + "name", a.Name),
                        new XElement(k + "styleUrl", "#wf-area"),
                        string.IsNullOrWhiteSpace(a.Notes)
                            ? null
                            : new XElement(k + "description", new XCData(Wayfarer.Util.RichNotes.Normalize(a.Notes)!)),
                        new XElement(k + "Polygon",
                            new XElement(k + "tessellate", 1),
                            new XElement(k + "outerBoundaryIs",
                                new XElement(k + "LinearRing",
                                    new XElement(k + "coordinates", coordsText)
                                )
                            )
                        )
                    );

                    folder.Add(placemark);
                }

                doc.Add(folder);
            }

            /* 3 ── Segments as lines ------------------------------------------- */
            foreach (var s in (trip.Segments ?? Enumerable.Empty<Segment>()).OrderBy(s => s.DisplayOrder))
            {
                if (s.RouteGeometry is not LineString line) continue;

                // --- friendly title / description --------------------------------
                var from = (trip.Regions ?? Enumerable.Empty<Region>()).SelectMany(r => r.Places ?? Enumerable.Empty<Place>())
                    .FirstOrDefault(p => p.Id == s.FromPlaceId);
                var to = (trip.Regions ?? Enumerable.Empty<Region>()).SelectMany(r => r.Places ?? Enumerable.Empty<Place>())
                    .FirstOrDefault(p => p.Id == s.ToPlaceId);

                string fromTxt = from == null ? "Start" : $"{from.Name} ({from.Region?.Name})";
                string toTxt = to == null ? "End" : $"{to.Name} ({to.Region?.Name})";

                string etaTxt = s.EstimatedDuration.HasValue
                    ? $"{s.EstimatedDuration:hh\\:mm} h"
                    : "";
                string modeTxt = string.IsNullOrWhiteSpace(s.Mode) ? "segment" : s.Mode;

                string title = $"{fromTxt} → {toTxt}";
                string desc = $"{etaTxt} by {modeTxt}".Trim();

                doc.Add(
                    new XElement(k + "Placemark",
                        new XElement(k + "name", title),
                        new XElement(k + "styleUrl", "#wf-line"),
                        string.IsNullOrWhiteSpace(desc)
                            ? null
                            : new XElement(k + "description", new XCData(desc)),
                        new XElement(k + "LineString",
                            new XElement(k + "tessellate", 1),
                            new XElement(k + "coordinates",
                                string.Join(" ",
                                    line.Coordinates.Select(c => $"{c.X},{c.Y},0")))))
                );
            }

            /* wrap <kml> + attach wf namespace --------------------------------- */
            var kml = new XElement(k + "kml",
                new XAttribute(XNamespace.Xmlns + "wf", wf),
                doc);

            return new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                kml).ToString();
        }

    }
}
