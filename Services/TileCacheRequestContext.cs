using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

public partial class TileCacheService
{
    private static readonly string[] _nonPublicHostSuffixes =
    [
        ".localhost",
        ".local",
        ".internal",
        ".home.arpa",
        ".test",
        ".invalid",
        ".example",
        ".onion",
        ".alt"
    ];
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
            var requestHost = NormalizePublicDnsHost(context.Request.Host.Host);
            if (requestHost == null ||
                !GetAuthorizedHosts(_configuration).Contains(
                    requestHost,
                    StringComparer.OrdinalIgnoreCase))
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
        GetAuthorizedHosts(configuration).Length > 0;

    /// <summary>Returns normalized exact public hostnames, excluding wildcard and special-use values.</summary>
    private static string[] GetAuthorizedHosts(IConfiguration configuration) =>
        (configuration["AllowedHosts"] ?? string.Empty)
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(NormalizePublicDnsHost)
        .Where(host => host != null)
        .Cast<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>Normalizes one exact public DNS hostname or rejects local and special-use names.</summary>
    private static string? NormalizePublicDnsHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Contains('*', StringComparison.Ordinal))
        {
            return null;
        }

        var normalized = host.EndsWith(".", StringComparison.Ordinal)
            ? host[..^1]
            : host;
        if (normalized.EndsWith(".", StringComparison.Ordinal) ||
            Uri.CheckHostName(normalized) != UriHostNameType.Dns ||
            !normalized.Contains(".", StringComparison.Ordinal) ||
            normalized.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            _nonPublicHostSuffixes.Any(suffix =>
                normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return normalized.ToLowerInvariant();
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
