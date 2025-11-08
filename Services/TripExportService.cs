using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
        // Static semaphore to prevent concurrent Chrome downloads across all instances
        private static readonly SemaphoreSlim _downloadLock = new(1, 1);

        readonly ApplicationDbContext _db;
        readonly MapSnapshotService _snap;
        readonly IHttpContextAccessor _ctx;
        readonly LinkGenerator _link;
        readonly IRazorViewRenderer _razor;
        readonly BrowserFetcher _browserFetcher;
        readonly ILogger<TripExportService> _logger;
        readonly IConfiguration _configuration;
        readonly SseService _sseService;
        readonly string _chromeCachePath;
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        public TripExportService(
            ApplicationDbContext dbContext,
            MapSnapshotService mapSnapshot,
            IHttpContextAccessor httpContextAccessor,
            LinkGenerator linkGenerator,
            IRazorViewRenderer razor,
            ILogger<TripExportService> logger,
            IConfiguration configuration,
            SseService sseService)
        {
            _db = dbContext;
            _snap = mapSnapshot;
            _ctx = httpContextAccessor;
            _link = linkGenerator;
            _razor = razor;
            _logger = logger;
            _configuration = configuration;
            _sseService = sseService;

            // Get Chrome cache directory from configuration (defaults to ChromeCache if not specified)
            _chromeCachePath = configuration["CacheSettings:ChromeCacheDirectory"] ?? "ChromeCache";

            // Resolve to absolute path and normalize path separators for current platform
            _chromeCachePath = Path.GetFullPath(_chromeCachePath);

            _logger.LogInformation("Chrome cache directory for PDF export configured at: {ChromePath}", _chromeCachePath);

            // Initialize BrowserFetcher with custom download path
            // BrowserFetcher auto-detects platform: Windows (x64/ARM64), macOS (x64/ARM64), Linux (x64)
            // Note: Chrome doesn't provide ARM64 Linux binaries - handled separately in GetChromeBinaryAsync()
            _browserFetcher = new BrowserFetcher(new BrowserFetcherOptions
            {
                Path = _chromeCachePath
            });
        }

        /// <summary>
        /// Gets the Chrome/Chromium executable path. Downloads automatically for supported platforms.
        /// For ARM64 Linux, looks for system-installed Chromium.
        /// </summary>
        private async Task<string> GetChromeBinaryAsync()
        {
            var isLinuxArm64 = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
                              RuntimeInformation.OSArchitecture == Architecture.Arm64;

            if (isLinuxArm64)
            {
                // Chrome doesn't provide ARM64 Linux binaries - must use system Chromium
                _logger.LogInformation("ARM64 Linux detected. Searching for system-installed Chromium...");

                var chromiumPaths = new[]
                {
                    "/usr/bin/chromium-browser",
                    "/usr/bin/chromium",
                    "/snap/bin/chromium",
                    "/usr/bin/google-chrome",
                    "/usr/bin/google-chrome-stable"
                };

                var systemChromium = chromiumPaths.FirstOrDefault(File.Exists);

                if (systemChromium != null)
                {
                    _logger.LogInformation("Using system Chromium at: {ChromiumPath}", systemChromium);
                    return systemChromium;
                }

                // No Chromium found - throw clear error with installation instructions
                throw new InvalidOperationException(
                    "PDF Export requires Chromium browser on ARM64 Linux (e.g., Raspberry Pi).\n" +
                    "Chrome doesn't provide official ARM64 Linux binaries.\n\n" +
                    "To enable PDF export, install Chromium:\n" +
                    "  sudo apt-get update && sudo apt-get install -y chromium-browser\n\n" +
                    "Searched paths: " + string.Join(", ", chromiumPaths));
            }

            // For all other supported platforms (Windows x64/ARM64, macOS x64/ARM64, Linux x64)
            // Check if Chrome is already installed before attempting download

            _logger.LogInformation("Checking for Chrome browser for PDF generation...");

            // GetInstalledBrowsers returns available browsers WITHOUT downloading
            var installedBrowsers = _browserFetcher.GetInstalledBrowsers();
            var chromeBrowser = installedBrowsers.FirstOrDefault();

            if (chromeBrowser != null)
            {
                // Chrome already exists - use it directly
                var executablePath = chromeBrowser.GetExecutablePath();
                _logger.LogInformation("Chrome browser found at: {ExecutablePath}", executablePath);
                return executablePath;
            }

            // Chrome doesn't exist - download it (use semaphore to prevent concurrent downloads)
            _logger.LogInformation("Chrome not found, downloading...");
            await _downloadLock.WaitAsync();
            try
            {
                // Double-check after acquiring lock (another request might have downloaded it)
                installedBrowsers = _browserFetcher.GetInstalledBrowsers();
                chromeBrowser = installedBrowsers.FirstOrDefault();

                if (chromeBrowser != null)
                {
                    _logger.LogInformation("Chrome was downloaded by another request");
                    return chromeBrowser.GetExecutablePath();
                }

                // Actually download Chrome
                var newBrowser = await _browserFetcher.DownloadAsync();
                var newExecPath = newBrowser.GetExecutablePath();
                _logger.LogInformation("Chrome browser downloaded to: {ExecutablePath}", newExecPath);

                // Clean up unused Chrome variants AFTER download completes
                // BrowserFetcher may download both Chrome and ChromeHeadlessShell - we only need Chrome
                CleanupUnusedChromeBinaries(_chromeCachePath);

                return newExecPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download Chrome browser for PDF export");
                throw new InvalidOperationException(
                    "Could not download Chrome browser required for PDF export. " +
                    "Check server logs for details. Common causes: " +
                    "1) No internet connection to download Chrome, " +
                    "2) Insufficient disk permissions to write to ChromeCache directory, " +
                    "3) Missing system libraries (libnss3, libgbm1, libatk-bridge2.0-0, etc.)", ex);
            }
            finally
            {
                _downloadLock.Release();
            }
        }

        /// <summary>
        /// Removes ChromeHeadlessShell if it exists, keeping only the standard Chrome browser
        /// </summary>
        private void CleanupUnusedChromeBinaries(string chromeCachePath)
        {
            try
            {
                var headlessShellPath = Path.Combine(chromeCachePath, "ChromeHeadlessShell");
                if (Directory.Exists(headlessShellPath))
                {
                    _logger.LogInformation("Removing unused ChromeHeadlessShell to save disk space (~240MB)...");
                    Directory.Delete(headlessShellPath, recursive: true);
                    _logger.LogInformation("ChromeHeadlessShell removed successfully");
                }
            }
            catch (Exception ex)
            {
                // Don't fail if cleanup fails - it's not critical
                _logger.LogWarning(ex, "Failed to cleanup ChromeHeadlessShell directory (non-critical)");
            }
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
                .Include(t => t.Regions).ThenInclude(r => r.Areas)
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
        /// PDF Exporter with optional real-time progress reporting via SSE
        /// </summary>
        /// <param name="tripId">The trip to export</param>
        /// <param name="progressChannel">Optional SSE channel for progress updates</param>
        /// <returns>PDF stream</returns>
        /// <exception cref="KeyNotFoundException"></exception>
        public async Task<Stream> GeneratePdfGuideAsync(Guid tripId, string? progressChannel = null)
        {
            // Helper to send progress updates if channel is provided
            async Task ReportProgress(string message)
            {
                if (!string.IsNullOrEmpty(progressChannel))
                {
                    await _sseService.BroadcastAsync(progressChannel,
                        System.Text.Json.JsonSerializer.Serialize(new { message }));
                }
            }

            await ReportProgress("🗺️ Loading trip data...");

            /* 1 ── load trip + related data ------------------------------------ */
            var trip = await _db.Trips
                           .Include(t => t.User)
                           .FirstOrDefaultAsync(t => t.Id == tripId)
                       ?? throw new KeyNotFoundException($"Trip not found: {tripId}");

            var regions = await _db.Regions.Include(r => r.Areas).Where(r => r.TripId == tripId).ToListAsync();
            var places = await _db.Places.Where(p => p.Region.TripId == tripId).ToListAsync();
            var segments = await _db.Segments.Where(s => s.TripId == tripId).ToListAsync();

            await ReportProgress($"📊 Found {regions.Count} regions, {places.Count} places, {segments.Count} segments");

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
                await ReportProgress("📷 Downloading cover photo...");
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
            await ReportProgress("📸 Capturing trip overview map...");
            snap["trip"] = await _snap.CaptureMapAsync(
                BuildMapUrl(lat, lon, zoom, isPub, trip.Id),
                800, 800, cookie);

            // regions
            if (regions.Count > 0)
            {
                await ReportProgress($"🗺️ Capturing {regions.Count} region maps...");
                var regionIndex = 0;
                foreach (var r in regions)
                {
                    if (r.Center == null) continue;
                    regionIndex++;
                    await ReportProgress($"  📍 Region {regionIndex}/{regions.Count}: {r.Name}");
                    snap[$"region_{r.Id}"] = await _snap.CaptureMapAsync(
                        BuildMapUrl(r.Center.Y, r.Center.X, 10, isPub, trip.Id),
                        600, 600, cookie);
                }
            }

            // places
            if (places.Count > 0)
            {
                await ReportProgress($"📌 Capturing {places.Count} place maps...");
                var placeIndex = 0;
                var placesWithLocation = places.Where(p => p.Location != null).ToList();

                foreach (var p in placesWithLocation)
                {
                    placeIndex++;
                    var placeName = !string.IsNullOrWhiteSpace(p.Name) ? $" - {p.Name}" : "";
                    await ReportProgress($"  📍 Place {placeIndex}/{placesWithLocation.Count}{placeName}");

                    snap[$"place_{p.Id}"] = await _snap.CaptureMapAsync(
                        BuildMapUrl(p.Location!.Y, p.Location.X, 15, isPub, trip.Id),
                        600, 600, cookie);
                }
            }

            // segments (mid-points)
            if (segments.Count > 0)
            {
                await ReportProgress($"🛣️ Capturing {segments.Count} route segments...");
                var segmentIndex = 0;
                var validSegments = segments.Where(s =>
                {
                    var from = places.FirstOrDefault(p => p.Id == s.FromPlaceId);
                    var to = places.FirstOrDefault(p => p.Id == s.ToPlaceId);
                    return from?.Location != null && to?.Location != null;
                }).ToList();

                foreach (var s in validSegments)
                {
                    var from = places.First(p => p.Id == s.FromPlaceId);
                    var to = places.First(p => p.Id == s.ToPlaceId);

                    segmentIndex++;
                    var routeName = $"{from.Name} → {to.Name}";
                    await ReportProgress($"  🚗 Segment {segmentIndex}/{validSegments.Count}: {routeName}");

                    double midLat = (from.Location!.Y + to.Location!.Y) / 2;
                    double midLon = (from.Location.X + to.Location.X) / 2;

                    snap[$"segment_{s.Id}"] = await _snap.CaptureMapAsync(
                        BuildMapUrl(midLat, midLon, 11, isPub, trip.Id, segmentId: s.Id.ToString()),
                        600, 600, cookie);
                }
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
            await ReportProgress("📝 Rendering PDF template...");
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


            // Puppeteer ➜ PDF - Get Chrome/Chromium executable (auto-downloads for supported platforms)
            await ReportProgress("🌐 Starting PDF generator...");
            var executablePath = await GetChromeBinaryAsync();

            await using var browser = await Puppeteer.LaunchAsync(
                new LaunchOptions
                {
                    Headless = true,
                    ExecutablePath = executablePath
                });
            await using var page = await browser.NewPageAsync();

            await ReportProgress("📄 Generating PDF document...");
            await page.SetContentAsync(html,
                new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.Networkidle0 } });

            var pdfBytes = await page.PdfDataAsync(new PdfOptions
            {
                Format = PaperFormat.A4,
                MarginOptions = new MarginOptions
                    { Top = "30mm", Bottom = "15mm", Left = "12mm", Right = "12mm" },
                PrintBackground = true,
                DisplayHeaderFooter = true,
                HeaderTemplate = "<span></span>",
                FooterTemplate     = @"
<div style=""width:100%;margin:0;padding:0;
               font-family:'Segoe UI',Arial,sans-serif;
               font-size:10pt;color:#555;
               text-align:center;"">
  Page <span class=""pageNumber""></span> of <span class=""totalPages""></span>
</div>"
            });

            await ReportProgress("✅ PDF ready! Starting download...");
            return new MemoryStream(pdfBytes);
        }

        /* ---------------------------------------------------------------- helpers */

        string BuildMapUrl(double lat, double lon, int zoom, bool pub, Guid id, string? segmentId = null)
        {
            string uri;

            if (pub)
            {
                // Public trips use custom route: /Public/Trips/{id}
                // Can't use LinkGenerator because it uses controller/action naming, not custom routes
                var request = _ctx.HttpContext!.Request;
                var baseUrl = $"{request.Scheme}://{request.Host}";
                var query = $"?lat={lat.ToString("F6")}&lon={lon.ToString("F6")}&zoom={zoom}";
                if (segmentId != null)
                    query += $"&seg={segmentId}";
                uri = $"{baseUrl}/Public/Trips/{id}{query}";
            }
            else
            {
                // Private trips use standard route: /User/Trip/View/{id}
                uri = _link.GetUriByAction(
                    _ctx.HttpContext!,
                    action: "View",
                    controller: "Trip",
                    values: new
                    {
                        area = "User",
                        id,
                        lat = lat.ToString("F6"),
                        lon = lon.ToString("F6"),
                        zoom,
                        seg = segmentId
                    }) ?? throw new InvalidOperationException("Unable to create User/Trip/View URL");
            }

            return uri + "&print=1"; // ?print=1 hides sidebar for snapshots
        }
    }
}