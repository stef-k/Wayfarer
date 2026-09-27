using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace Wayfarer.Services;

/// <summary>Defines the sole server-owned browser origin and pins its transport to local Kestrel.</summary>
internal sealed class BrowserCapturePolicy
{
    internal Uri Origin { get; }
    internal string HostResolverRule => $"MAP {Origin.Host} 127.0.0.1";

    private BrowserCapturePolicy(Uri origin) => Origin = origin;

    /// <summary>Fails closed unless both the existing public-host authority and an HTTP listener are configured.</summary>
    internal static BrowserCapturePolicy? Resolve(IConfiguration configuration)
    {
        var host = TileCacheService.GetFirstAuthorizedPublicHost(configuration);
        if (host == null) return null;
        var listeners = configuration["Kestrel:Endpoints:Http:Url"] ?? configuration["urls"] ??
            Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        foreach (var candidate in (listeners ?? "").Split(';'))
        {
            var normalized = candidate.Trim().Replace("http://*:", "http://0.0.0.0:")
                .Replace("http://+:", "http://0.0.0.0:");
            if (Uri.TryCreate(normalized, UriKind.Absolute, out var listener) &&
                listener.Scheme == "http" && listener.Port > 0 && listener.AbsolutePath == "/" &&
                string.IsNullOrEmpty(listener.UserInfo) && string.IsNullOrEmpty(listener.Query))
                return new BrowserCapturePolicy(new UriBuilder("http", host, listener.Port).Uri);
        }
        return null;
    }

    /// <summary>Accepts only the exact capture authority; credentials and alternate ports are forbidden.</summary>
    internal bool Allows(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Origin.Scheme && uri.Host == Origin.Host && uri.Port == Origin.Port &&
        string.IsNullOrEmpty(uri.UserInfo);

    /// <summary>Resolves a relative application path without allowing authority replacement.</summary>
    internal string Url(string path)
    {
        if (!path.StartsWith('/') || path.StartsWith("//") || path.Contains('\\'))
            throw new ArgumentException("Capture requires a relative application path.", nameof(path));
        var url = new Uri(Origin, path).AbsoluteUri;
        if (!Allows(url)) throw new ArgumentException("Invalid capture path.", nameof(path));
        return url;
    }

    /// <summary>Transfers only the reassembled Identity application cookie, including chunked-cookie support.</summary>
    internal IReadOnlyList<Cookie> ApplicationCookies(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var name = options.Cookie.Name!;
        var value = options.CookieManager.GetRequestCookie(context, name);
        if (string.IsNullOrEmpty(value)) return [];
        // Preserve the cookie manager's original chunks; Chromium rejects oversized individual cookies.
        var names = new List<string> { name };
        var original = context.Request.Cookies[name];
        if (original?.StartsWith("chunks-", StringComparison.Ordinal) == true &&
            int.TryParse(original.AsSpan(7), out var count))
            for (var i = 1; i <= count; i++) names.Add($"{name}C{i}");
        return names.Select(key => new Cookie
        {
            Name = key, Value = context.Request.Cookies[key]!, Domain = Origin.Host, Path = "/", HttpOnly = true,
            Secure = false, SameSite = SameSiteAttribute.Lax
        }).ToArray();
    }

    /// <summary>Installs context-wide interception before any page exists, including popups and workers.</summary>
    internal async Task ConfigureAsync(IBrowserContext context)
    {
        context.SetDefaultTimeout(30000);
        context.SetDefaultNavigationTimeout(30000);
        await context.RouteWebSocketAsync("**/*", socket => socket.CloseAsync());
        await context.RouteAsync("**/*", async route =>
        {
            try
            {
                if (!Allows(route.Request.Url))
                {
                    await route.AbortAsync("blockedbyclient");
                    return;
                }
                // Fetch does not use Chromium's resolver. Pin transport independently and preserve Host/cookies.
                var target = new UriBuilder(route.Request.Url) { Host = "127.0.0.1" };
                var headers = await route.Request.AllHeadersAsync();
                headers["host"] = Origin.Authority;
                var response = await route.FetchAsync(new RouteFetchOptions
                {
                    Url = target.Uri.AbsoluteUri, Headers = headers, MaxRedirects = 0, Timeout = 30000
                });
                try
                {
                    // No supported capture resource needs a redirect, including login redirects.
                    if (response.Status is >= 300 and < 400) await route.AbortAsync("blockedbyclient");
                    else await route.FulfillAsync(new RouteFulfillOptions { Response = response });
                }
                finally { await response.DisposeAsync(); }
            }
            catch (PlaywrightException)
            {
                // A closed context or failed first-party fetch must never fall through to direct networking.
                try { await route.AbortAsync("failed"); } catch (PlaywrightException) { }
            }
        });
    }

    /// <summary>Closes thumbnail dimension and cache-identity cardinality at every generation boundary.</summary>
    internal static bool IsThumbnailSize(string? size) => size is "320x180" or "800x450";

    /// <summary>Rejects unsupported internal thumbnail dimensions before cache lookup.</summary>
    internal static void ValidateThumbnail(int width, int height)
    {
        if ((width, height) is not ((320, 180) or (800, 450)))
            throw new ArgumentOutOfRangeException(nameof(width), "Supported thumbnails are 320x180 and 800x450.");
    }

    /// <summary>PDF maps have only the overview and detail variants.</summary>
    internal static void ValidateMap(int width, int height)
    {
        if ((width, height) is not ((800, 800) or (600, 600)))
            throw new ArgumentOutOfRangeException(nameof(width), "Unsupported PDF map dimensions.");
    }
}
