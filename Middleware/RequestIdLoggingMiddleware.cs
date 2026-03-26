using Serilog.Context;

namespace Wayfarer.Middleware;

/// <summary>
/// Pushes HttpContext.TraceIdentifier into Serilog's LogContext so every log entry
/// within the request pipeline includes the RequestId property automatically.
/// </summary>
public class RequestIdLoggingMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>
    /// Initializes a new instance of <see cref="RequestIdLoggingMiddleware"/>.
    /// </summary>
    public RequestIdLoggingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>
    /// Pushes the request's TraceIdentifier as a "RequestId" property into Serilog's
    /// LogContext, then invokes the next middleware. The property is automatically
    /// removed when the request completes.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        using (LogContext.PushProperty("RequestId", context.TraceIdentifier))
        {
            await _next(context);
        }
    }
}
