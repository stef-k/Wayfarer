using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Wayfarer.Parsers;
using Wayfarer.Util;

namespace Wayfarer.Middleware;

/// <summary>Enforces upload policy after authorization and before MVC antiforgery/model binding.</summary>
public sealed class UserFileUploadMiddleware(RequestDelegate next)
{
    /// <summary>Rejects disabled/oversized uploads without reading multipart data or staging files.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<UserFileUploadAttribute>() is null)
        {
            await next(context);
            return;
        }

        // Resolve lazily: unrelated routes and rejected authorization never query upload settings.
        var settings = context.RequestServices.GetRequiredService<IApplicationSettingsService>().GetSettings();
        var limit = UploadRequestPolicy.EffectiveBytes(settings.UploadSizeLimitMB);
        if (limit == 0)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize is long endpointLimit)
            limit = Math.Min(limit, endpointLimit);
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature?.MaxRequestBodySize is long currentLimit)
            limit = Math.Min(limit, currentLimit);
        if (context.Request.ContentLength > limit)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        // A read-only feature is safe only when its already-active ceiling is sufficiently strict.
        if (feature is null || (feature.IsReadOnly &&
            (feature.MaxRequestBodySize is null || feature.MaxRequestBodySize > limit)))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        if (!feature.IsReadOnly)
            feature.MaxRequestBodySize = limit;

        await next(context);
    }
}
