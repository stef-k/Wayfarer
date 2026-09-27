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
        // Error/status handlers re-enter this pipeline after body consumption. A read-only
        // feature is safe only when its already-active ceiling remains bounded by our maximum.
        if (feature is null || (feature.IsReadOnly &&
            (feature.MaxRequestBodySize is null || feature.MaxRequestBodySize > UploadRequestPolicy.MaximumRequestBytes)))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (!feature.IsReadOnly)
            feature.MaxRequestBodySize = UploadRequestPolicy.MaximumRequestBytes;
        if (context.Request.ContentLength > UploadRequestPolicy.MaximumRequestBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        await next(context);
    }
}
