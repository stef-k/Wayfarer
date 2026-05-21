using Wayfarer.Services;

namespace Wayfarer.Middleware;

/// <summary>
/// Adds the compiled Wayfarer version to normal HTTP responses.
/// </summary>
public sealed class AppVersionHeaderMiddleware
{
    /// <summary>
    /// The response header that carries the Wayfarer version.
    /// </summary>
    public const string HeaderName = "X-Wayfarer-Version";

    private readonly RequestDelegate _next;
    private readonly IAppVersionProvider _appVersionProvider;

    /// <summary>
    /// Creates middleware that appends the Wayfarer version header.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="appVersionProvider">The provider for the compiled application version.</param>
    public AppVersionHeaderMiddleware(RequestDelegate next, IAppVersionProvider appVersionProvider)
    {
        _next = next;
        _appVersionProvider = appVersionProvider;
    }

    /// <summary>
    /// Registers the version header before downstream middleware starts the response.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var (httpContext, version) = ((HttpContext, string))state;
            httpContext.Response.Headers[HeaderName] = version;
            return Task.CompletedTask;
        }, (context, _appVersionProvider.Version));

        await _next(context);
    }
}
