using Microsoft.AspNetCore.Http.Features;

namespace Wayfarer.Middleware
{
    public class DynamicRequestSizeMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly long _maxRequestSize;

        public DynamicRequestSizeMiddleware(RequestDelegate next, long maxRequestSize)
        {
            _next = next;
            _maxRequestSize = maxRequestSize;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = _maxRequestSize;
            await _next(context);
        }
    }
}