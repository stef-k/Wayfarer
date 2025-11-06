using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;

// for ResourceType

namespace Wayfarer.Parsers
{
    public sealed class MapSnapshotService
    {
        readonly BrowserFetcher _fetcher;
        readonly ILogger<MapSnapshotService> _logger;

        /// <summary>
        /// Initializes MapSnapshotService with configured Chrome cache directory
        /// </summary>
        public MapSnapshotService(ILogger<MapSnapshotService> logger, IConfiguration configuration)
        {
            _logger = logger;

            // Get Chrome cache directory from configuration (defaults to ChromeCache if not specified)
            var chromeCachePath = configuration["CacheSettings:ChromeCacheDirectory"] ?? "ChromeCache";

            // Resolve to absolute path and normalize path separators for current platform
            chromeCachePath = Path.GetFullPath(chromeCachePath);

            _logger.LogInformation("Chrome cache directory configured at: {ChromePath}", chromeCachePath);

            // Initialize BrowserFetcher with custom download path
            // PuppeteerSharp automatically detects platform (Windows/Linux/Mac) and architecture (x64/ARM)
            _fetcher = new BrowserFetcher(new BrowserFetcherOptions
            {
                Path = chromeCachePath
            });

            // Clean up unused Chrome variants (ChromeHeadlessShell) to save disk space
            CleanupUnusedChromeBinaries(chromeCachePath);
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

        /// <summary>
        /// Captures a full‐page PNG screenshot of <paramref name="url"/> at the given viewport.
        /// </summary>
        /// <summary>
        /// Captures a full‐page PNG of the given map URL, proxying any Google My Maps assets
        /// through our own /Public/ProxyImage endpoint (using absolute URLs).
        /// </summary>
        public async Task<byte[]> CaptureMapAsync(string url, int width, int height, IList<CookieParam>? cookies = null)
        {
            // 0) derive origin for absolute proxy logging (not strictly needed for RespondAsync)
            var pageUri = new Uri(url);
            var origin = pageUri.GetLeftPart(UriPartial.Authority);

            // 1) ensure Chromium is downloaded
            try
            {
                _logger.LogInformation("Checking for Chrome browser...");
                var installedBrowser = await _fetcher.DownloadAsync();
                _logger.LogInformation("Chrome browser ready at: {ExecutablePath}", installedBrowser.GetExecutablePath());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download Chrome browser. Check network connectivity and disk permissions.");
                throw new InvalidOperationException(
                    "Could not download Chrome browser required for PDF export. " +
                    "Check server logs for details. Common causes: " +
                    "1) No internet connection to download Chrome, " +
                    "2) Insufficient disk permissions to write to ~/.local/share/puppeteer/, " +
                    "3) Missing system libraries (libnss3, libgbm1, etc.)", ex);
            }

            // 2) launch headless with web‐security off
            await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = true,
                Args = new[]
                {
                    "--ignore-certificate-errors",
                    "--disable-web-security",
                    "--disable-features=IsolateOrigins,site-per-process",
                    "--disable-features=SameSiteByDefaultCookies,CookiesWithoutSameSiteMustBeSecure"
                }
            });

            // 3) new page + viewport
            await using var page = await browser.NewPageAsync();
            await page.SetViewportAsync(new ViewPortOptions { Width = width, Height = height });

            // 4) logging
            page.Console += (_, e) => Console.WriteLine($"[page] {e.Message.Text}");
            page.RequestFailed += (_, e) => Console.WriteLine($"[REQ FAIL] {e.Request.Url} → {e.Request.FailureText}");

            // 5) intercept + proxy My Maps images
            await page.SetRequestInterceptionAsync(true);
            page.Request += async (_, e) =>
            {
                var req = e.Request;
                Console.WriteLine($"[REQ] ({req.ResourceType}) → {req.Url}");

                if (req.ResourceType == ResourceType.Image
                    && req.Url.Contains("mymaps.usercontent.google.com"))
                {
                    Console.WriteLine($"⤷ proxying image {req.Url}");
                    try
                    {
                        // fetch bytes server-side
                        using var http = new HttpClient();
                        using var response = await http.GetAsync(req.Url);
                        var bytes = await response.Content.ReadAsByteArrayAsync();
                        var contentType = response.Content.Headers.ContentType?.MediaType
                                          ?? "application/octet-stream";

                        // respond with those bytes
                        await req.RespondAsync(new ResponseData
                        {
                            Status = response.StatusCode,
                            ContentType = contentType,
                            BodyData = bytes
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] proxy failed for {req.Url}: {ex}");
                        await req.AbortAsync(RequestAbortErrorCode.Failed);
                    }
                }
                else
                {
                    await req.ContinueAsync();
                }
            };

            // 6) set auth cookies if needed
            if (cookies?.Any() == true)
                await page.SetCookieAsync(cookies.ToArray());

            // 7) navigate + wait for network idle
            await page.GoToAsync(url, new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.Networkidle0 }
            });

            // 8) try to pick up the leaflet-image dataURI, fallback to screenshot
            byte[] imageBytes;
            try
            {
                var handle = await page.WaitForFunctionAsync(
                    "() => window.__leafletImageUrl",
                    new WaitForFunctionOptions
                    {
                        Polling = WaitForFunctionPollingOption.Raf,
                        Timeout = 30_000 // 30s
                    });

                var dataUri = await handle.JsonValueAsync<string>();
                var comma = dataUri.IndexOf(',');
                var b64 = comma >= 0
                    ? dataUri.Substring(comma + 1)
                    : dataUri;

                imageBytes = Convert.FromBase64String(b64);
            }
            catch (WaitTaskTimeoutException)
            {
                Console.WriteLine("[WARN] leaflet-image dataUri timed out — falling back to screenshot");
                imageBytes = await page.ScreenshotDataAsync(new ScreenshotOptions
                {
                    Type = ScreenshotType.Png,
                    FullPage = false
                });
            }

            return imageBytes;
        }
    }
}