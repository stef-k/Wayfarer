using System.Text.RegularExpressions;
using System.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Wayfarer.Models.ViewModels;
using Wayfarer.Services;

namespace Wayfarer.Parsers;

/// <summary>Owns bounded PDF orchestration independently of the KML serialization path.</summary>
public partial class TripExportService
{
    /// <summary>Test seam for the shipped browser lifecycle, without replacing PDF orchestration.</summary>
    internal Func<Task<IPlaywright>>? BrowserFactory { get; init; }

    /// <summary>Clock seam for proving the fixed server deadline without waiting five minutes.</summary>
    internal TimeProvider DeadlineClock { get; init; } = TimeProvider.System;

    /// <summary>Holds one nonwaiting admission lease and a linked five-minute deadline for the whole export.</summary>
    public async Task<Stream> GeneratePdfGuideAsync(Guid tripId, string? progressChannel = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var admission = BrowserAdmission.Shared.TryAcquire()
            ?? throw new BrowserUnavailableException("Browser capacity is full. Retry shortly.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5), DeadlineClock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try { return await GeneratePdfCoreAsync(tripId, progressChannel, linked.Token); }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (Exception) when (deadline.IsCancellationRequested)
        { throw new BrowserUnavailableException("PDF generation exceeded the five-minute deadline."); }
        catch (PlaywrightException)
        { throw new BrowserUnavailableException("PDF browser capture failed. Retry shortly."); }
        catch (TimeoutException)
        { throw new BrowserUnavailableException("PDF browser operation timed out. Retry shortly."); }
    }

    /// <summary>
    /// PDF Exporter with optional real-time progress reporting via SSE
    /// </summary>
    /// <param name="tripId">The trip to export</param>
    /// <param name="progressChannel">Optional SSE channel for progress updates</param>
    /// <param name="cancellationToken">Cancellation token to abort PDF generation</param>
    /// <returns>PDF stream</returns>
    /// <exception cref="KeyNotFoundException"></exception>
    private async Task<Stream> GeneratePdfCoreAsync(Guid tripId, string? progressChannel = null, CancellationToken cancellationToken = default)
    {
        // Helper to send progress updates if channel is provided
        async Task ReportProgress(string message)
        {
            if (!string.IsNullOrEmpty(progressChannel))
            {
                await _sseService.BroadcastAsync(progressChannel,
                    System.Text.Json.JsonSerializer.Serialize(new { message })).WaitAsync(cancellationToken);
            }
        }

        await ReportProgress("🗺️ Loading trip data...");
        cancellationToken.ThrowIfCancellationRequested();

        /* 1 ── load trip + related data ------------------------------------ */
        var trip = await _db.Trips
                       .Include(t => t.User)
                       .FirstOrDefaultAsync(t => t.Id == tripId, cancellationToken)
                   ?? throw new KeyNotFoundException($"Trip not found: {tripId}");

        var regions = await _db.Regions.Include(r => r.Areas).Where(r => r.TripId == tripId).OrderBy(r => r.DisplayOrder).ThenBy(r => r.Id).ToListAsync(cancellationToken);
        var places = await _db.Places.Where(p => p.Region.TripId == tripId).OrderBy(p => p.DisplayOrder).ThenBy(p => p.Id).ToListAsync(cancellationToken);
        // The separate PDF reader explicitly loads each authorized journey anchor.
        var segments = await _db.Segments.Include(s => s.FromPlace).Include(s => s.ToPlace).Include(s => s.Waypoints.OrderBy(w => w.Position)).ThenInclude(w => w.Place)
            .Where(s => s.TripId == tripId).OrderBy(s => s.DisplayOrder).ThenBy(s => s.Id).ToListAsync(cancellationToken);

        await ReportProgress($"📊 Found {regions.Count} regions, {places.Count} places, {segments.Count} segments");
        cancellationToken.ThrowIfCancellationRequested();

        var policy = BrowserCapturePolicy.Resolve(_configuration)
            ?? throw new BrowserUnavailableException("No trusted capture listener is configured.");
        var authCookie = trip.IsPublic ? null : policy.ApplicationCookies(_ctx.HttpContext!);
        await using var workflow = await BrowserWorkflow.StartAsync(policy, cancellationToken, BrowserFactory);

        /* 3 ── snapshots dictionary --------------------------------------- */
        var snap = new Dictionary<string, byte[]>();
        string? coverDataUri = null;
        var budgetReported = false;
        async Task ReportMapBudget()
        {
            if (budgetReported) return;
            budgetReported = true;
            _logger.LogInformation("PDF map-image budget reached for trip {TripId}", tripId);
            await ReportProgress("Map-image limit reached (64); the complete itinerary will be exported.");
        }


        // cover photo (download once)
        if (!string.IsNullOrWhiteSpace(trip.CoverImageUrl))
        {
            await ReportProgress("📷 Downloading cover photo...");
            coverDataUri = await TripExportCoverSnapshotBuilder.BuildDataUriAsync(_imageProxyService, trip.CoverImageUrl, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();

        int zoom = trip.Zoom ?? 2;
        double lat = trip.CenterLat ?? 0;
        double lon = trip.CenterLon ?? 0;

        bool isPub = trip.IsPublic;
        var cookie = isPub ? null : authCookie;

        // trip overview
        await ReportProgress("📸 Capturing trip overview map...");
        snap["trip"] = await _snap.CaptureMapAsync(workflow,
            BuildMapUrl(lat, lon, zoom, isPub, trip.Id),
            800, 800, cookie, cancellationToken);

        // regions
        if (regions.Count > 0)
        {
            await ReportProgress($"🗺️ Capturing {regions.Count} region maps...");
            var regionIndex = 0;
            foreach (var r in regions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (r.Center == null) continue;
                if (snap.Count >= 64) { await ReportMapBudget(); break; }
                regionIndex++;
                await ReportProgress($"  📍 Region {regionIndex}/{regions.Count}: {r.Name}");
                snap[$"region_{r.Id}"] = await _snap.CaptureMapAsync(workflow,
                    BuildMapUrl(r.Center.Y, r.Center.X, 10, isPub, trip.Id),
                    600, 600, cookie, cancellationToken);
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
                cancellationToken.ThrowIfCancellationRequested();
                if (snap.Count >= 64) { await ReportMapBudget(); break; }
                placeIndex++;
                var placeName = !string.IsNullOrWhiteSpace(p.Name) ? $" - {p.Name}" : "";
                await ReportProgress($"  📍 Place {placeIndex}/{placesWithLocation.Count}{placeName}");

                snap[$"place_{p.Id}"] = await _snap.CaptureMapAsync(workflow,
                    BuildMapUrl(p.Location!.Y, p.Location.X, 15, isPub, trip.Id),
                    600, 600, cookie, cancellationToken);
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
                cancellationToken.ThrowIfCancellationRequested();
                if (snap.Count >= 64) { await ReportMapBudget(); break; }
                var from = places.First(p => p.Id == s.FromPlaceId);
                var to = places.First(p => p.Id == s.ToPlaceId);

                segmentIndex++;
                var routeName = $"{from.Name} → {to.Name}";
                await ReportProgress($"  🚗 Segment {segmentIndex}/{validSegments.Count}: {routeName}");

                double midLat = (from.Location!.Y + to.Location!.Y) / 2;
                double midLon = (from.Location.X + to.Location.X) / 2;

                snap[$"segment_{s.Id}"] = await _snap.CaptureMapAsync(workflow,
                    BuildMapUrl(midLat, midLon, 11, isPub, trip.Id, segmentId: s.Id.ToString()),
                    600, 600, cookie, cancellationToken);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();

        /* 4 ── render PDF -------------------------------------------------- */

        // ADD helper – inline for brevity
        string ToDataUri(byte[] png) =>
            $"data:image/png;base64,{Convert.ToBase64String(png)}";

        // convert snapshots dictionary to data-URI strings
        var snapUris = snap.ToDictionary(kvp => kvp.Key, kvp => ToDataUri(kvp.Value));
        if (coverDataUri is not null) snapUris["cover"] = coverDataUri;

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
            "~/Views/Trip/Print.cshtml", vm).WaitAsync(cancellationToken);

        var baseUrl = policy.Origin.GetLeftPart(UriPartial.Authority);
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


        html = html.Replace("<head>", $"<head><base href=\"{baseUrl}/\">");

        // Playwright ➜ PDF
        await ReportProgress("🌐 Starting PDF generator...");

        cancellationToken.ThrowIfCancellationRequested();

        await using var pdfContext = await workflow.NewContextAsync(800, 800, cookie);
        var page = await pdfContext.Context.NewPageAsync();
        await ReportProgress("📄 Generating PDF document...");
        await page.SetContentAsync(html, new PageSetContentOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });
        cancellationToken.ThrowIfCancellationRequested();

        var pdfBytes = await page.PdfAsync(new PagePdfOptions
        {
            Format = "A4",
            Margin = new Margin
            {
                Top = "30mm",
                Bottom = "15mm",
                Left = "12mm",
                Right = "12mm"
            },
            PrintBackground = true,
            DisplayHeaderFooter = true,
            HeaderTemplate = "<span></span>",
            FooterTemplate = @"
<div style=""width:100%;margin:0;padding:0;
   font-family:'Segoe UI',Arial,sans-serif;
   font-size:10pt;color:#555;
   text-align:center;"">
  Page <span class=""pageNumber""></span> of <span class=""totalPages""></span>
</div>"
        }).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        await ReportProgress("✅ PDF ready! Starting download...");
        cancellationToken.ThrowIfCancellationRequested();
        return new MemoryStream(pdfBytes);
    }

    /* ---------------------------------------------------------------- helpers */

    /// <summary>Builds only relative first-party map paths with culture-independent coordinates.</summary>
    internal static string BuildMapUrl(double lat, double lon, int zoom, bool pub, Guid id, string? segmentId = null)
    {
        var path = pub ? $"/Public/Trips/{id}" : $"/User/Trip/View/{id}";
        return $"{path}?lat={lat.ToString("F6", CI)}&lon={lon.ToString("F6", CI)}&zoom={zoom}" +
            (segmentId == null ? "" : $"&seg={Uri.EscapeDataString(segmentId)}") + "&print=1";
    }
}
