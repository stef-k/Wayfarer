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
    public async Task InvokeAsync(HttpContext context)
    {
        bool isApi = context.Request.Path.StartsWithSegments("/api");

        try
        {
            await _next(context);

            if (isApi && !context.Response.HasStarted && context.Response.StatusCode is 401 or 403 or 404)
            {
                int statusCode = context.Response.StatusCode;
                context.Response.Clear();
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    status = statusCode,
                    error = statusCode switch { 401 => "Unauthorized", 403 => "Forbidden", _ => "Not Found" },
                    message = statusCode switch
                    {
                        401 => "Authentication is required to access this endpoint.",
                        403 => "You do not have permission to access this resource.",
                        _ => "The requested API endpoint does not exist."
                    }
                }));
            }
        }
        catch (Exception ex) when (isApi && !context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                status = 500,
                error = "Internal Server Error",
                message = "An unexpected error occurred.",
                details = ex.Message
            }));
        }
    }
}
