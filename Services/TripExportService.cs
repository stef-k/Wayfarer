using System.Globalization;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using PuppeteerSharp;
using PuppeteerSharp.Media;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using static Wayfarer.Parsers.KmlMappings;

namespace Wayfarer.Parsers
{
    /// <summary>Generates PDF and KML exports for a Trip.</summary>
    public class TripExportService : ITripExportService
    {
        readonly ApplicationDbContext _db;
        readonly MapSnapshotService _snap;
        readonly IHttpContextAccessor _ctx;
        readonly LinkGenerator _link;
        readonly IRazorViewRenderer _razor;
        readonly BrowserFetcher _browserFetcher;
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        public TripExportService(
            ApplicationDbContext dbContext,
            MapSnapshotService mapSnapshot,
            IHttpContextAccessor httpContextAccessor,
            LinkGenerator linkGenerator,
            IRazorViewRenderer razor)
        {
            _db = dbContext;
            _snap = mapSnapshot;
            _ctx = httpContextAccessor;
            _link = linkGenerator;
            _razor = razor;
            _browserFetcher = new BrowserFetcher();
        }

        /* ---------------------------------------------------------------- KML stubs */

        public string GenerateWayfarerKml(Guid tripId)
        {
            var trip = _db.Trips
                           .Include(t => t.Regions).ThenInclude(r => r.Places)
                           .Include(t => t.Regions).ThenInclude(r => r.Areas)
                           .Include(t => t.Segments)
                           .AsNoTracking()
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
                .Include(t => t.Segments)
                .AsNoTracking()
                .First(t => t.Id == tripId);

            /* namespaces --------------------------------------------------------- */
            XNamespace k = "http://www.opengis.net/kml/2.2";
            XNamespace wf = "https://wayfarer.stefk.me/kml"; // private → never shown

            /* root                                                                */
            var doc = new XElement(k + "Document",
                new XElement(k + "name", trip.Name));

            /* 1 ── basic icon + line styles ------------------------------------- */
            var iconStyles = trip.Regions
                .SelectMany(r => r.Places ?? Enumerable.Empty<Place>())
                .Select(p => (p.IconName, p.MarkerColor))
                .Distinct()
                .Select(ic =>
                {
                    IconMapping.TryGetValue(ic.IconName, out var shape);
                    shape ??= "placemark_circle";

                    ColorMapping.TryGetValue(ic.MarkerColor, out var clr);
                    clr ??= "ff000000";

                    var href = $"http://maps.google.com/mapfiles/kml/shapes/{shape}.png";
                    return new XElement(k + "Style",
                        new XAttribute("id", $"wf_{ic.IconName}_{ic.MarkerColor}"),
                        new XElement(k + "IconStyle",
                            new XElement(k +"color", clr),
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

            doc.Add(iconStyles);
            doc.Add(lineStyle);

            /* 2 ── Regions → Folders ------------------------------------------- */
            foreach (var (reg, idx) in trip.Regions
                         .OrderBy(r => r.DisplayOrder)
                         .Select((r, i) => (r, i)))
            {
                var folder = new XElement(k + "Folder",
                    new XElement(k + "name", $"{idx + 1:00} – {reg.Name}"));

                /* 2a ── Places --------------------------------------------------- */
                foreach (var p in reg.Places.OrderBy(p => p.DisplayOrder))
                {
                    if (p.Location == null) continue;

                    folder.Add(
                        new XElement(k + "Placemark",
                            new XElement(k + "name", p.Name),
                            new XElement(k + "styleUrl", "#wf-icon"),
                            /* description (wrapped in CDATA) */
                            string.IsNullOrWhiteSpace(p.Notes)
                                ? null
                                : new XElement(k + "description", new XCData(p.Notes)),
                            /* hidden place-id */
                            new XElement(k + "ExtendedData",
                                new XElement(wf + "PlaceId", p.Id)),
                            /* geometry */
                            new XElement(k + "Point",
                                new XElement(k + "coordinates",
                                    $"{p.Location.X},{p.Location.Y},0")))
                    );
                }
                
                /* 2b ── Areas as Polygons -------------------------------------------- */
                foreach (var a in reg.Areas?.OrderBy(a => a.DisplayOrder) ?? Enumerable.Empty<Area>())
                {
                    if (a.Geometry is not Polygon poly) continue;

                    var coordsText = string.Join(" ",
                        poly.Coordinates.Select(c =>
                            $"{c.X.ToString(CI)},{c.Y.ToString(CI)},0"));

                    var placemark = new XElement(k + "Placemark",
                        new XElement(k + "name", a.Name),
                        string.IsNullOrWhiteSpace(a.Notes)
                            ? null
                            : new XElement(k + "description", new XCData(a.Notes)),
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
            foreach (var s in trip.Segments.OrderBy(s => s.DisplayOrder))
            {
                if (s.RouteGeometry is not LineString line) continue;

                // --- friendly title / description --------------------------------
                var from = trip.Regions.SelectMany(r => r.Places)
                    .FirstOrDefault(p => p.Id == s.FromPlaceId);
                var to = trip.Regions.SelectMany(r => r.Places)
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

        /// <summary>
        /// PDF Exporter
        /// </summary>
        /// <param name="tripId"></param>
        /// <returns></returns>
        /// <exception cref="KeyNotFoundException"></exception>
        public async Task<Stream> GeneratePdfGuideAsync(Guid tripId)
        {
            /* 1 ── load trip + related data ------------------------------------ */
            var trip = await _db.Trips
                           .Include(t => t.User)
                           .FirstOrDefaultAsync(t => t.Id == tripId)
                       ?? throw new KeyNotFoundException($"Trip not found: {tripId}");

            var regions = await _db.Regions.Where(r => r.TripId == tripId).ToListAsync();
            var places = await _db.Places.Where(p => p.Region.TripId == tripId).ToListAsync();
            var segments = await _db.Segments.Where(s => s.TripId == tripId).ToListAsync();

            /* 2 ── auth-cookies for private (User/) trips ---------------------- */
            var req = _ctx.HttpContext!.Request;
            var host = req.Host.Host;
            var authCookie = req.Cookies.Select(p => new CookieParam
            {
                Name = p.Key,
                Value = p.Value,
                Domain = host,
                Path = "/"
            }).ToList();

            /* 3 ── snapshots dictionary --------------------------------------- */
            var snap = new Dictionary<string, byte[]>();

            // cover photo (download once)
            if (!string.IsNullOrWhiteSpace(trip.CoverImageUrl))
            {
                try
                {
                    using var http = new HttpClient();
                    snap["cover"] = await http.GetByteArrayAsync(trip.CoverImageUrl);
                }
                catch
                {
                    /* ignore – cover simply omitted on failure */
                }
            }

            int zoom = trip.Zoom ?? 2;
            double lat = trip.CenterLat ?? 0;
            double lon = trip.CenterLon ?? 0;

            bool isPub = trip.IsPublic;
            var cookie = isPub ? null : authCookie;

            // trip overview
            snap["trip"] = await _snap.CaptureMapAsync(
                BuildMapUrl(lat, lon, zoom, isPub, trip.Id),
                800, 800, cookie);

            // regions
            foreach (var r in regions)
            {
                if (r.Center == null) continue;
                snap[$"region_{r.Id}"] = await _snap.CaptureMapAsync(
                    BuildMapUrl(r.Center.Y, r.Center.X, 10, isPub, trip.Id),
                    600, 600, cookie);
            }

            // places
            foreach (var p in places)
            {
                if (p.Location == null) continue;
                snap[$"place_{p.Id}"] = await _snap.CaptureMapAsync(
                    BuildMapUrl(p.Location.Y, p.Location.X, 15, isPub, trip.Id),
                    600, 600, cookie);
            }

            // segments (mid-points)
            foreach (var s in segments)
            {
                var from = places.FirstOrDefault(p => p.Id == s.FromPlaceId);
                var to = places.FirstOrDefault(p => p.Id == s.ToPlaceId);
                if (from?.Location == null || to?.Location == null) continue;

                double midLat = (from.Location.Y + to.Location.Y) / 2;
                double midLon = (from.Location.X + to.Location.X) / 2;

                snap[$"segment_{s.Id}"] = await _snap.CaptureMapAsync(
                    BuildMapUrl(midLat, midLon, 11, isPub, trip.Id, segmentId: s.Id.ToString()),
                    600, 600, cookie);
            }

            /* 4 ── render PDF -------------------------------------------------- */

            // ADD helper – inline for brevity
            string ToDataUri(byte[] png) =>
                $"data:image/png;base64,{Convert.ToBase64String(png)}";

            // convert snapshots dictionary to data-URI strings
            var snapUris = snap.ToDictionary(kvp => kvp.Key, kvp => ToDataUri(kvp.Value));

            // build view-model
            var vm = new TripPrintViewModel
            {
                Trip = trip,
                Regions = regions,
                Places = places,
                Segments = segments,
                Snap = snapUris
            };

            // Razor ➜ HTML
            var html = await _razor.RenderViewToStringAsync(
                "~/Views/Trip/Print.cshtml", vm);

            var baseUrl = $"{req.Scheme}://{req.Host}";
            html = Regex.Replace(html,
                "<img([^>]+?)src=[\"'](?<url>https?://[^\"']+)[\"']",
                m =>
                {
                    var encoded = HttpUtility.UrlEncode(m.Groups["url"].Value);
                    return m.Value.Replace(
                        m.Groups["url"].Value,
                        $"{baseUrl}/Public/ProxyImage?url={encoded}" // ← now absolute
                    );
                },
                RegexOptions.IgnoreCase);


            // Puppeteer ➜ PDF
            await _browserFetcher.DownloadAsync(); // once, then cached
            await using var browser = await Puppeteer.LaunchAsync(
                new LaunchOptions { Headless = true });
            await using var page = await browser.NewPageAsync();

            await page.SetContentAsync(html,
                new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.Networkidle0 } });

            var pdfBytes = await page.PdfDataAsync(new PdfOptions
            {
                Format = PaperFormat.A4,
                MarginOptions = new MarginOptions
                    { Top = "15mm", Bottom = "15mm", Left = "10mm", Right = "10mm" },
                PrintBackground = true
            });

            return new MemoryStream(pdfBytes);
        }

        /* ---------------------------------------------------------------- helpers */

        string BuildMapUrl(double lat, double lon, int zoom, bool pub, Guid id, string? segmentId = null)
        {
            var area = pub ? "Public" : "User";

            var uri = _link.GetUriByAction(
                _ctx.HttpContext!,
                action: "View",
                controller: "Trip",
                values: new
                {
                    area,
                    id,
                    lat = lat.ToString("F6"),
                    lon = lon.ToString("F6"),
                    zoom,
                    seg = segmentId
                }) ?? throw new InvalidOperationException("Unable to create Trip/View URL");

            return uri + "&print=1"; // ?print=1 hides sidebar for snapshots
        }
    }
}