using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>
/// Request-owned incoming token authority. Preserves legacy parsing and matching,
/// and never retains a positive authentication result across HTTP requests.
/// </summary>
public class IncomingApiTokenResolver(ApplicationDbContext db, ApiWorkAdmission admission)
{
    private static readonly object RequestKey = new();
    private Task<ApplicationUser?>? _resolution;
    private string? _token;

    /// <summary>Gets the single authority shared by all bearer adapters in this request.</summary>
    public static IncomingApiTokenResolver ForRequest(HttpContext context, ApplicationDbContext db)
    {
        if (context.Items.TryGetValue(RequestKey, out var value)) return (IncomingApiTokenResolver)value!;
        var resolver = new IncomingApiTokenResolver(db, ApiWorkAdmission.TokenLookups);
        context.Items[RequestKey] = resolver;
        return resolver;
    }

    /// <summary>Explicitly resets request resolution for the existing mobile reset adapter.</summary>
    public void Reset() => _resolution = null;

    /// <summary>Overload status recorded for the MVC boundary, including actions that catch exceptions.</summary>
    public int DeniedStatus { get; private set; }

    /// <summary>Reads already-resolved overload state without resolving anonymous endpoints.</summary>
    public static int GetDeniedStatus(HttpContext context) =>
        context.Items.TryGetValue(RequestKey, out var value) ? ((IncomingApiTokenResolver)value!).DeniedStatus : 0;

    /// <summary>Resolves the loose legacy Authorization value once per request.</summary>
    public Task<ApplicationUser?> ResolveAsync(HttpContext context, CancellationToken cancellationToken = default) =>
        ResolveTokenAsync(context.Request.Headers.Authorization.FirstOrDefault()?.Split(' ').Last(),
            RateLimitHelper.GetClientIpAddress(context), cancellationToken);

    /// <summary>Resolves a supplied token using the same activity and admission authority.</summary>
    public Task<ApplicationUser?> ResolveTokenAsync(string? token, string effectiveIp,
        CancellationToken cancellationToken = default)
    {
        if (_resolution != null && _token == token) return _resolution;
        _token = token;
        return _resolution = ResolveCoreAsync(token, effectiveIp, cancellationToken);
    }

    /// <summary>Admits one uncached lookup without queuing or retaining token-identity buckets.</summary>
    private async Task<ApplicationUser?> ResolveCoreAsync(string? token, string effectiveIp,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token)) return null;
        using var permit = admission.TryAcquire(effectiveIp, out var status);
        if (permit == null)
        {
            DeniedStatus = status;
            throw new ApiTokenAdmissionException();
        }

        return await LookupAsync(token, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Database seam executed only while a lookup permit is held.</summary>
    protected virtual async Task<ApplicationUser?> LookupAsync(string token, CancellationToken cancellationToken)
    {
        // Admission precedes hashing and every database lookup. The process limiter retains only live IP buckets.
        var hash = ApiTokenService.HashToken(token);
        var row = await db.ApiTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash
            || t.Token == token && t.Name.Trim().ToLower() != "mapbox", cancellationToken).ConfigureAwait(false);
        if (row?.UserId == null) return null;
        var user = await db.Users.Include(u => u.ApiTokens)
            .FirstOrDefaultAsync(u => u.Id == row.UserId, cancellationToken).ConfigureAwait(false);
        return user is { IsActive: true } ? user : null;
    }
}

/// <summary>Stops action work when lookup capacity is exhausted; translated by the MVC filter.</summary>
public sealed class ApiTokenAdmissionException : Exception;
