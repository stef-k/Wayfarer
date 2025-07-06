using PuppeteerSharp;

namespace Wayfarer.Services;

public sealed class MapSnapshotService
{
    readonly BrowserFetcher _fetcher = new();

    /// <summary>
    /// Captures a full-page PNG screenshot of <paramref name="url"/> at the given viewport.
    /// </summary>
    public async Task<byte[]> CaptureMapAsync(
        string url, int width, int height, IList<CookieParam>? cookies = null)
    {
        await _fetcher.DownloadAsync(); // cached under ~/.local-chromium

        await using var browser = await Puppeteer.LaunchAsync(
            new LaunchOptions { Headless = true });

        await using var page = await browser.NewPageAsync();
        await page.SetViewportAsync(new ViewPortOptions
        {
            Width = width,
            Height = height
        });
        
        page.Console += (_, e) => Console.WriteLine($"[page] {e.Message.Text}");

        if (cookies?.Any() == true)
            await page.SetCookieAsync(cookies.ToArray());
        
        // 0) navigate – the page itself will kick off leaflet-image
        await page.GoToAsync(url, WaitUntilNavigation.DOMContentLoaded);

        // 1) wait until the client code exposes the PNG data-URI
        var dataUriHandle = await page.WaitForFunctionAsync(
            "() => window.__leafletImageUrl ?? null",
            new WaitForFunctionOptions
            {
                Polling = WaitForFunctionPollingOption.Raf,
                Timeout = 0   // let the page decide when it's truly ready
            });

        var dataUri = await dataUriHandle.JsonValueAsync<string>();

        // 2) strip the header and return raw bytes
        var comma = dataUri.IndexOf(',');
        var b64   = comma >= 0 ? dataUri[(comma + 1)..] : dataUri;
        return Convert.FromBase64String(b64);
    }
}