using System.Text.Json;

namespace Wayfarer.Middleware;

/// <summary>
/// Converts terminal API authorization and not-found responses into JSON without changing their HTTP status.
/// </summary>
public sealed class ApiErrorResponseMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>
    /// Initializes the middleware with the next pipeline delegate.
    /// </summary>
    public ApiErrorResponseMiddleware(RequestDelegate next) => _next = next;

    /// <summary>
    /// Processes API error responses and preserves their original status after clearing the response body.
    /// </summary>
    public async Task InvokeAsync(HttpContext context, ILogger<ApiErrorResponseMiddleware> logger)
    {
        bool isApi = context.Request.Path.StartsWithSegments("/api");

        try
        {
            await _next(context);

            if (isApi && !context.Response.HasStarted && context.Response.StatusCode is 401 or 403 or 404)
            {
                int statusCode = context.Response.StatusCode;
                ClearPreservingCookies(context.Response);
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    status = statusCode,
                    error = statusCode switch { 401 => "Unauthorized", 403 => "Forbidden", _ => "Not Found" },
                    message = statusCode switch
                    {
                        401 when Services.ActiveAccountCookie.WasRejected(context) => Services.ActiveAccountCookie.SessionEndedMessage,
                        401 => "Authentication is required to access this endpoint.",
                        403 => "You do not have permission to access this resource.",
                        _ => "The requested API endpoint does not exist."
                    }
                }));
            }
        }
        // The managed adapter owns bounded mutation-uncertainty/storage errors, including its early replay path.
        catch (Exception) when (isApi && context.GetEndpoint()?.Metadata.GetMetadata<ManagedImportAttribute>() is not null)
        { throw; }
        catch (Exception ex) when (isApi && !context.Response.HasStarted)
        {
            // Keep exception payloads out of both the public envelope and routine diagnostics.
            logger.LogError("Unhandled API failure {ExceptionType} for {Method} {Path}; request {RequestId}",
                ex.GetType().Name, context.Request.Method, context.Request.Path, context.TraceIdentifier);
            ClearPreservingCookies(context.Response);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                status = 500,
                error = "Internal Server Error",
                message = "An unexpected error occurred.",
                details = "The request could not be completed.",
                requestId = context.TraceIdentifier
            }));
        }
    }

    /// <summary>Keep sign-out and other cookie lifecycle headers when replacing an API error body.</summary>
    private static void ClearPreservingCookies(HttpResponse response)
    {
        var cookies = response.Headers.SetCookie;
        response.Clear();
        if (cookies.Count > 0) response.Headers.SetCookie = cookies;
    }
}
