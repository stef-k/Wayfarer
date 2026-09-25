using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Wayfarer.Services;

namespace Wayfarer.Parsers
{
    public sealed class MapSnapshotService
    {
        readonly ILogger<MapSnapshotService> _logger;

        /// <summary>
        /// Initializes map capture; browser provisioning is an explicit deployment responsibility.
        /// </summary>
        public MapSnapshotService(ILogger<MapSnapshotService> logger, IConfiguration configuration)
        {
            _logger = logger;
        }

        /// <summary>
        /// Captures a full-page PNG screenshot of the given map URL, proxying any Google My Maps assets
        /// through our own /Public/ProxyImage endpoint (using absolute URLs).
        /// </summary>
        /// <param name="url">The URL to capture</param>
        /// <param name="width">Viewport width</param>
        /// <param name="height">Viewport height</param>
        /// <param name="cookies">Optional cookies for authentication</param>
        /// <param name="cancellationToken">Cancellation token to abort the operation</param>
        public async Task<byte[]> CaptureMapAsync(string url, int width, int height,
            IList<Cookie>? cookies = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 0) derive origin for absolute proxy logging
            var pageUri = new Uri(url);
            var origin = pageUri.GetLeftPart(UriPartial.Authority);

            // 1) Create Playwright instance
            using var playwright = await Playwright.CreateAsync();
            cancellationToken.ThrowIfCancellationRequested();

            var launchArgs = new List<string>
            {
                "--ignore-certificate-errors",
                "--disable-web-security",
                "--disable-features=IsolateOrigins,site-per-process",
                "--disable-features=SameSiteByDefaultCookies,CookiesWithoutSameSiteMustBeSecure"
            };

            // ARM64 Linux specific flags
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
                RuntimeInformation.OSArchitecture == Architecture.Arm64)
            {
                launchArgs.AddRange(new[]
                {
                    "--no-sandbox",
                    "--disable-dev-shm-usage",
                    "--disable-gpu"
                });
            }

            // 2) Launch headless browser
            var browser = await BrowserRuntime.LaunchAsync(playwright, new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = launchArgs
            });
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // 3) Create new page with viewport
                var page = await browser.NewPageAsync(new BrowserNewPageOptions
                {
                    ViewportSize = new ViewportSize { Width = width, Height = height }
                });
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // 4) Logging
                    page.Console += (_, msg) => Console.WriteLine($"[page] {msg.Text}");
                    page.RequestFailed += (_, req) => Console.WriteLine($"[REQ FAIL] {req.Url} → {req.Failure}");

                    // 5) Intercept + proxy My Maps images
                    await page.RouteAsync("**/*", async route =>
                    {
                        var request = route.Request;
                        Console.WriteLine($"[REQ] ({request.ResourceType}) → {request.Url}");

                        if (request.ResourceType == "image" &&
                            request.Url.Contains("mymaps.usercontent.google.com"))
                        {
                            Console.WriteLine($"⤷ proxying image {request.Url}");
                            try
                            {
                                // fetch bytes server-side
                                using var http = new HttpClient();
                                using var response = await http.GetAsync(request.Url, cancellationToken);
                                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                                var contentType = response.Content.Headers.ContentType?.MediaType
                                                  ?? "application/octet-stream";

                                // respond with those bytes
                                await route.FulfillAsync(new RouteFulfillOptions
                                {
                                    Status = (int)response.StatusCode,
                                    ContentType = contentType,
                                    BodyBytes = bytes
                                });
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[ERROR] proxy failed for {request.Url}: {ex}");
                                await route.AbortAsync("failed");
                            }
                        }
                        else
                        {
                            await route.ContinueAsync();
                        }
                    });

                    // 6) Set auth cookies if needed
                    if (cookies?.Any() == true)
                    {
                        await page.Context.AddCookiesAsync(cookies);
                    }
                    cancellationToken.ThrowIfCancellationRequested();

                    // 7) Navigate + wait for network idle
                    await page.GotoAsync(url, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.NetworkIdle,
                        Timeout = 30000
                    });
                    cancellationToken.ThrowIfCancellationRequested();

                    // 8) Try to pick up the leaflet-image dataURI, fallback to screenshot
                    byte[] imageBytes;
                    try
                    {
                        var dataUri = await page.EvaluateAsync<string>(
                            @"() => new Promise((resolve, reject) => {
                                const checkInterval = setInterval(() => {
                                    if (window.__leafletImageUrl) {
                                        clearInterval(checkInterval);
                                        resolve(window.__leafletImageUrl);
                                    }
                                }, 100);
                                setTimeout(() => {
                                    clearInterval(checkInterval);
                                    reject(new Error('Timeout waiting for __leafletImageUrl'));
                                }, 30000);
                            })"
                        );
                        cancellationToken.ThrowIfCancellationRequested();

                        var comma = dataUri.IndexOf(',');
                        var b64 = comma >= 0
                            ? dataUri.Substring(comma + 1)
                            : dataUri;

                        imageBytes = Convert.FromBase64String(b64);
                    }
                    catch (TimeoutException)
                    {
                        Console.WriteLine("[WARN] leaflet-image dataUri timed out — falling back to screenshot");
                        imageBytes = await page.ScreenshotAsync(new PageScreenshotOptions
                        {
                            Type = ScreenshotType.Png,
                            FullPage = false
                        });
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    return imageBytes;
                }
                finally
                {
                    await page.CloseAsync();
                }
            }
            finally
            {
                await browser.CloseAsync();
            }
        }
    }
}
