using Microsoft.EntityFrameworkCore;
using PuppeteerSharp;
using PuppeteerSharp.Media;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
namespace Wayfarer.Services
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

        public string GenerateWayfarerKml(Guid tripId) =>
            "<kml xmlns=\"http://www.opengis.net/kml/2.2\"></kml>";

        public string GenerateMyMapsKml(Guid tripId) =>
            "<kml xmlns=\"http://www.opengis.net/kml/2.2\"></kml>";

        /* ---------------------------------------------------------------- PDF */

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
                    BuildMapUrl(r.Center.Y, r.Center.X, 11, isPub, trip.Id),
                    600, 600, cookie);
            }

            // places
            foreach (var p in places)
            {
                if (p.Location == null) continue;
                snap[$"place_{p.Id}"] = await _snap.CaptureMapAsync(
                    BuildMapUrl(p.Location.Y, p.Location.X, 16, isPub, trip.Id),
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