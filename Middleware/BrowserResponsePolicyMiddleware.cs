using Microsoft.AspNetCore.Diagnostics;

namespace Wayfarer.Middleware;

/// <summary>Owns browser response headers, allowing only explicitly approved public embed documents.</summary>
public sealed class BrowserResponsePolicyMiddleware(RequestDelegate next)
{
    private static readonly object EmbedKey = new();
    private static readonly object PrivateKey = new();

    /// <summary>Retains private download policy through downstream error re-execution.</summary>
    public static void RequirePrivateResponse(HttpContext context) => context.Items[PrivateKey] = true;

    /// <summary>Marks a public embed after its controller has completed visibility checks.</summary>
    public static void AllowPublicEmbed(HttpContext context) => context.Items[EmbedKey] = true;

    /// <summary>Applies one final policy even when downstream error handling clears or re-executes a response.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        // Preserve the original connection-page policy through downstream short circuits and error re-execution.
        var connectionPage = context.Request.Path.StartsWithSegments("/User/ApiToken", StringComparison.OrdinalIgnoreCase);
        // Fence the original private route before authentication, errors or status-code re-execution.
        var path = context.Request.Path.Value ?? "";
        var privatePlanning = path.StartsWith("/api/trips/", StringComparison.OrdinalIgnoreCase)
            && (path.Contains("/planning", StringComparison.OrdinalIgnoreCase) || path.Contains("/editor", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/conditional/", StringComparison.OrdinalIgnoreCase) || path.Contains("/deletion", StringComparison.OrdinalIgnoreCase))
            || path.StartsWith("/Trip/ExportPersonalWayfarerKml/", StringComparison.OrdinalIgnoreCase)
            || path.TrimEnd('/').Equals("/User/Trip/Import", StringComparison.OrdinalIgnoreCase)
            || path.TrimEnd('/').Equals("/User/Trip/ReviewImport", StringComparison.OrdinalIgnoreCase);
        // Capture photo/reference routes before authentication or error re-execution.
        var privatePhoto = path.StartsWith("/api/profile-photo/", StringComparison.OrdinalIgnoreCase)
            || ((path.StartsWith("/api/groups/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/mobile/groups/", StringComparison.OrdinalIgnoreCase))
                && path.Contains("/members", StringComparison.OrdinalIgnoreCase));
        var publicPhoto = path.StartsWith("/Public/Trips/", StringComparison.OrdinalIgnoreCase)
            && path.Contains("/owner-photo/", StringComparison.OrdinalIgnoreCase);
        context.Response.OnStarting(() =>
        {
            var response = context.Response;
            if (connectionPage)
            {
                response.Headers.CacheControl = "no-store";
                response.Headers.Pragma = "no-cache";
            }
            if (privatePlanning || privatePhoto || context.Items.ContainsKey(PrivateKey))
            {
                response.Headers.CacheControl = "private, no-store";
                response.Headers.Pragma = "no-cache";
                response.Headers.Remove("ETag");
                response.Headers.Remove("Last-Modified");
            }
            if (publicPhoto)
            {
                response.Headers.CacheControl = "no-store";
                response.Headers.Remove("ETag");
                response.Headers.Remove("Last-Modified");
            }
            var embed = context.Items.ContainsKey(EmbedKey)
                && response.StatusCode is >= 200 and < 300
                && response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true
                && context.Features.Get<IExceptionHandlerFeature>() == null
                && context.Features.Get<IStatusCodeReExecuteFeature>() == null;
            response.Headers.ContentSecurityPolicy = embed ? "frame-ancestors *" : "frame-ancestors 'self'";
            if (embed)
                response.Headers.Remove("X-Frame-Options");
            else
                response.Headers.XFrameOptions = "SAMEORIGIN";
            response.Headers.XContentTypeOptions = "nosniff";
            response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            return Task.CompletedTask;
        });
        await next(context);
    }
}
