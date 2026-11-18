using System.Net.Http.Headers;
using Wayfarer.Models;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>The strict Authorization syntax shared by /me and the newer mobile Group contracts.</summary>
internal static class MobileBearer
{
    /// <summary>Cookie credentials confer no authority and token-store errors propagate.</summary>
    internal static Task<ApplicationUser?> ResolveAsync(HttpContext context, ApplicationDbContext db, CancellationToken ct)
    {
        var values = context.Request.Headers.Authorization;
        if (values.Count != 1 || !AuthenticationHeaderValue.TryParse(values[0], out var authorization)
            || !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(authorization.Parameter) || authorization.Parameter.Any(char.IsWhiteSpace))
            return Task.FromResult<ApplicationUser?>(null);

        return IncomingApiTokenResolver.ForRequest(context, db)
            .ResolveTokenAsync(authorization.Parameter, RateLimitHelper.GetClientIpAddress(context), ct);
    }
}
