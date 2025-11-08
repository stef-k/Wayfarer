using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;

// for ResourceType

namespace Wayfarer.Parsers
{
    public sealed class MapSnapshotService
    {
        // Static semaphore to prevent concurrent Chrome downloads across all instances
        private static readonly SemaphoreSlim _downloadLock = new(1, 1);

        readonly BrowserFetcher _fetcher;
        readonly ILogger<MapSnapshotService> _logger;
        readonly string _chromeCachePath;

        /// <summary>
        /// Initializes MapSnapshotService with configured Chrome cache directory.
        /// Supports cross-platform Chrome download for Windows (x64/ARM64), macOS (x64/ARM64), and Linux (x64).
        /// For ARM64 Linux (e.g., Raspberry Pi), requires system-installed Chromium (see deployment docs).
        /// </summary>
        public MapSnapshotService(ILogger<MapSnapshotService> logger, IConfiguration configuration)
        {
            _logger = logger;

            // Get Chrome cache directory from configuration (defaults to ChromeCache if not specified)
            _chromeCachePath = configuration["CacheSettings:ChromeCacheDirectory"] ?? "ChromeCache";

            // Resolve to absolute path and normalize path separators for current platform
            _chromeCachePath = Path.GetFullPath(_chromeCachePath);

            _logger.LogInformation("Chrome cache directory configured at: {ChromePath}", _chromeCachePath);

            // Initialize BrowserFetcher with custom download path
            // BrowserFetcher auto-detects platform: Windows (x64/ARM64), macOS (x64/ARM64), Linux (x64)
            // Note: Chrome doesn't provide ARM64 Linux binaries - handled separately in GetChromeBinaryAsync()
            _fetcher = new BrowserFetcher(new BrowserFetcherOptions
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

            _logger.LogInformation("Checking for Chrome browser...");

            // GetInstalledBrowsers returns available browsers WITHOUT downloading
            var installedBrowsers = _fetcher.GetInstalledBrowsers();
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
                installedBrowsers = _fetcher.GetInstalledBrowsers();
                chromeBrowser = installedBrowsers.FirstOrDefault();

                if (chromeBrowser != null)
                {
                    _logger.LogInformation("Chrome was downloaded by another request");
                    return chromeBrowser.GetExecutablePath();
                }

                // Actually download Chrome
                var newBrowser = await _fetcher.DownloadAsync();
                var newExecPath = newBrowser.GetExecutablePath();
                _logger.LogInformation("Chrome browser downloaded to: {ExecutablePath}", newExecPath);

                // Clean up unused Chrome variants AFTER download completes
                // BrowserFetcher may download both Chrome and ChromeHeadlessShell - we only need Chrome
                CleanupUnusedChromeBinaries(_chromeCachePath);

                return newExecPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download Chrome browser");
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

            // 1) Get Chrome/Chromium executable (auto-downloads for supported platforms)
            var executablePath = await GetChromeBinaryAsync();

            // 2) launch headless with web‐security off
            await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = true,
                ExecutablePath = executablePath,
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