using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

public partial class TileCacheService
{
    private static readonly byte[] _schedulerIdentityKey =
        RandomNumberGenerator.GetBytes(32);

    /// <summary>Returns an HTTP(S) authority with no path, query, fragment, or user information.</summary>
    private static string? ResolvePublicRequestOrigin(HttpContext? context)
    {
        if (context == null ||
            !context.Request.Host.HasValue ||
            context.Request.Scheme is not ("http" or "https"))
        {
            return null;
        }

        try
        {
            var builder = new UriBuilder(
                context.Request.Scheme,
                context.Request.Host.Host,
                context.Request.Host.Port ?? -1,
                "/")
            {
                Query = string.Empty,
                Fragment = string.Empty,
                UserName = string.Empty,
                Password = string.Empty
            };
            return builder.Uri.AbsoluteUri;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    /// <summary>Builds a bounded opaque scheduler key using the request-rate identity rule.</summary>
    private static string ResolveSchedulerClientKey(HttpContext? context, string? clientIp)
    {
        var userId = context?.User.Identity?.IsAuthenticated == true
            ? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            : null;
        var source = !string.IsNullOrWhiteSpace(userId)
            ? $"user:{userId}"
            : $"ip:{clientIp ?? "unknown"}";
        return Convert.ToHexString(HMACSHA256.HashData(
            _schedulerIdentityKey,
            Encoding.UTF8.GetBytes(source)));
    }
}
