using Microsoft.Playwright;

namespace Wayfarer.Services;

/// <summary>Launches deployment-provisioned Chromium without installing or changing browser discovery.</summary>
internal static class BrowserRuntime
{
    /// <summary>
    /// Uses Playwright's version-matched bundle and externally supplied PLAYWRIGHT_BROWSERS_PATH.
    /// Playwright owns and cleans its process-scoped profiles/downloads in the OS temporary directory.
    /// </summary>
    internal static async Task<IBrowser> LaunchAsync(IPlaywright playwright, BrowserTypeLaunchOptions options)
    {
        try
        {
            return await playwright.Chromium.LaunchAsync(options);
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                "The preinstalled Wayfarer Chromium runtime could not launch. Provision Chromium with this " +
                "release's playwright.ps1 and its required OS libraries (including libasound.so.2 on Linux); " +
                "set PLAYWRIGHT_BROWSERS_PATH to that bundle before starting Wayfarer. " +
                "Wayfarer does not install browsers at runtime. See docs/20-Deployment.md.", exception);
        }
    }
}
