using Microsoft.AspNetCore.Diagnostics;

namespace Wayfarer.Middleware;

/// <summary>Owns browser response headers, allowing only explicitly approved public embed documents.</summary>
public sealed class BrowserResponsePolicyMiddleware(RequestDelegate next)
{
    private static readonly object EmbedKey = new();

    /// <summary>Marks a public embed after its controller has completed visibility checks.</summary>
    public static void AllowPublicEmbed(HttpContext context) => context.Items[EmbedKey] = true;

    /// <summary>Applies one final policy even when downstream error handling clears or re-executes a response.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        // Preserve the original connection-page policy through downstream short circuits and error re-execution.
        var connectionPage = context.Request.Path.StartsWithSegments("/User/ApiToken", StringComparison.OrdinalIgnoreCase);
        context.Response.OnStarting(() =>
        {
            var response = context.Response;
            if (connectionPage)
            {
                response.Headers.CacheControl = "no-store";
                response.Headers.Pragma = "no-cache";
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
