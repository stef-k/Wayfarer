using Microsoft.Playwright;
using Wayfarer.Services;

namespace Wayfarer.Parsers;

/// <summary>Captures fixed-size PDF maps inside the export's admitted browser workflow.</summary>
public sealed class MapSnapshotService
{
    /// <summary>Retains the existing service registration; browser ownership belongs to the PDF workflow.</summary>
    public MapSnapshotService(ILogger<MapSnapshotService> logger, IConfiguration configuration) { }

    /// <summary>Captures only a successful first-party document, with a fresh isolated context per map.</summary>
    internal async Task<byte[]> CaptureMapAsync(BrowserWorkflow workflow, string path, int width, int height,
        IEnumerable<Cookie>? cookies, CancellationToken cancellationToken)
    {
        BrowserCapturePolicy.ValidateMap(width, height);
        cancellationToken.ThrowIfCancellationRequested();
        var url = workflow.Policy.Url(path);
        await using var context = await workflow.NewContextAsync(width, height, cookies);
        var page = await context.NewPageAsync();
        var response = await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30000
        });
        cancellationToken.ThrowIfCancellationRequested();
        if (response?.Ok != true || response.Url != url || page.Url != url)
            throw new BrowserUnavailableException("Map capture navigation failed.");
        // The print page's Leaflet readiness signal is bounded; screenshots retain the existing map framing.
        try
        {
            await page.WaitForFunctionAsync("() => !!window.__leafletImageUrl", null,
                new PageWaitForFunctionOptions { Timeout = 30000 });
        }
        catch (TimeoutException)
        {
            // A missing canvas signal may fall back to the successfully loaded first-party map.
        }
        cancellationToken.ThrowIfCancellationRequested();
        return await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png, Timeout = 30000 });
    }
}
