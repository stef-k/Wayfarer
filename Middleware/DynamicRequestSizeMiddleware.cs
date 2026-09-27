using Microsoft.AspNetCore.Http.Features;
using Wayfarer.Util;

namespace Wayfarer.Middleware;

/// <summary>Applies the fixed application ceiling before any request body is consumed.</summary>
public sealed class DynamicRequestSizeMiddleware(RequestDelegate next)
{
    /// <summary>Sets the compatibility ceiling; later endpoint limits may further restrict it.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is null || feature.IsReadOnly)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        feature.MaxRequestBodySize = UploadRequestPolicy.MaximumRequestBytes;
        if (context.Request.ContentLength > UploadRequestPolicy.MaximumRequestBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        await next(context);
    }
}
