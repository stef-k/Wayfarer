using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

public partial class TileCacheService
{
    private static readonly byte[] _schedulerIdentityKey =
        RandomNumberGenerator.GetBytes(32);

    /// <summary>Returns an allowed public HTTP(S) origin with no private request data.</summary>
    private string? ResolvePublicRequestOrigin(HttpContext? context)
    {
        if (context == null ||
            !context.Request.Host.HasValue ||
            context.Request.Scheme is not ("http" or "https"))
        {
            return null;
        }

        try
        {
            var requestHost = context.Request.Host.Host;
            if (!IsPublicDnsHost(requestHost) ||
                !HostString.MatchesAny(
                    new StringSegment(requestHost),
                    GetAuthorizedHostPatterns(_configuration)))
            {
                return null;
            }

            var builder = new UriBuilder(
                context.Request.Scheme,
                requestHost,
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

    /// <summary>Returns whether configuration can authorize at least one trustworthy provider Referer.</summary>
    internal static bool HasTrustworthyAllowedHosts(IConfiguration configuration) =>
        GetAuthorizedHostPatterns(configuration).Count > 0;

    /// <summary>Returns configured host-filter patterns, excluding catch-all and private host values.</summary>
    private static IList<StringSegment> GetAuthorizedHostPatterns(IConfiguration configuration) =>
        (configuration["AllowedHosts"] ?? string.Empty)
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(pattern =>
            pattern is not "*" and not "0.0.0.0" and not "[::]" &&
            IsPublicDnsHost(pattern.StartsWith("*.", StringComparison.Ordinal)
                ? pattern[2..]
                : pattern))
        .Select(pattern => new StringSegment(pattern))
        .ToArray();

    /// <summary>Rejects IP literals, localhost, and other single-label/private DNS names.</summary>
    private static bool IsPublicDnsHost(string host) =>
        Uri.CheckHostName(host) == UriHostNameType.Dns &&
        host.Contains('.', StringComparison.Ordinal) &&
        !host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);

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
