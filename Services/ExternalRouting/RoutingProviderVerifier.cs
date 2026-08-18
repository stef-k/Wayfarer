using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Runs bounded OSRM route probes using only administrator-supplied coordinates.</summary>
public sealed class RoutingProviderVerifier : IRoutingProviderVerifier
{
    private const int MaximumProfiles = 8;
    private const double MaximumAnchorDeviationMetres = 25;
    private readonly ApplicationDbContext _dbContext;
    private readonly RoutingBoundedExecutor _executor;

    /// <summary>Initializes bounded verification.</summary>
    public RoutingProviderVerifier(ApplicationDbContext dbContext, RoutingBoundedExecutor executor)
        => (_dbContext, _executor) = (dbContext, executor);

    /// <inheritdoc />
    public async Task<RoutingVerificationResult> VerifyAsync(Guid providerId, int expectedVersion, uint expectedRowVersion, CancellationToken cancellationToken)
    {
        var provider = await _dbContext.Set<RoutingProviderConfiguration>().AsNoTracking()
            .Include(item => item.ProfileMappings).SingleOrDefaultAsync(item => item.Id == providerId, cancellationToken);
        if (provider == null || provider.ConfigurationVersion != expectedVersion || provider.RowVersion != expectedRowVersion
            || RoutingProviderStateResolver.Resolve(provider, false) is RoutingProviderState.Incomplete or RoutingProviderState.Invalid)
            return RoutingVerificationResult.Failure("provider-configuration-stale");
        var profiles = provider.ProfileMappings.Select(item => item.OsrmProfile).Distinct(StringComparer.Ordinal).ToArray();
        if (profiles.Length is 0 or > MaximumProfiles) return RoutingVerificationResult.Failure("provider-profile-count-invalid");
        var from = new RouteCoordinate(provider.VerificationFromLongitude!.Value, provider.VerificationFromLatitude!.Value);
        var to = new RouteCoordinate(provider.VerificationToLongitude!.Value, provider.VerificationToLatitude!.Value);
        foreach (var profile in profiles)
        {
            var request = OsrmRoutingAdapter.BuildRelativeRequest(profile, [from, to]);
            var execution = await _executor.GetJsonAsync(new Uri(provider.BaseEndpoint!), request,
                provider.ResponseSizeLimitBytes, TimeSpan.FromSeconds(5), cancellationToken);
            if (!execution.Succeeded) return RoutingVerificationResult.Failure(execution.ErrorCode!);
            using var response = JsonResponse(execution.Json!);
            var route = await OsrmRoutingAdapter.ParseAsync(response, cancellationToken);
            if (!route.Succeeded || route.Waypoints.Count != 2 || route.Geometry.Count > 1000
                || DistanceMetres(from, route.Waypoints[0]) > MaximumAnchorDeviationMetres
                || DistanceMetres(to, route.Waypoints[1]) > MaximumAnchorDeviationMetres)
                return RoutingVerificationResult.Failure("provider-verification-invalid");
        }

        var tracked = await _dbContext.Set<RoutingProviderConfiguration>().SingleAsync(item => item.Id == providerId, cancellationToken);
        if (tracked.ConfigurationVersion != expectedVersion || tracked.RowVersion != expectedRowVersion)
            return RoutingVerificationResult.Failure("provider-configuration-stale");
        tracked.VerifiedConfigurationVersion = tracked.ConfigurationVersion;
        tracked.VerificationStatus = "verified";
        tracked.VerificationResult = "All mapped profiles returned valid bounded routes.";
        try { await _dbContext.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return RoutingVerificationResult.Failure("provider-configuration-stale"); }
        return new RoutingVerificationResult(true, null, tracked.ConfigurationVersion, tracked.RowVersion);
    }

    private static HttpResponseMessage JsonResponse(byte[] json) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(json) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } }
    };

    private static double DistanceMetres(RouteCoordinate first, RouteCoordinate second)
    {
        const double radius = 6371000;
        var firstLat = first.Latitude * Math.PI / 180;
        var secondLat = second.Latitude * Math.PI / 180;
        var deltaLat = (second.Latitude - first.Latitude) * Math.PI / 180;
        var deltaLon = (second.Longitude - first.Longitude) * Math.PI / 180;
        var value = Math.Sin(deltaLat / 2) * Math.Sin(deltaLat / 2)
            + Math.Cos(firstLat) * Math.Cos(secondLat) * Math.Sin(deltaLon / 2) * Math.Sin(deltaLon / 2);
        return radius * 2 * Math.Atan2(Math.Sqrt(value), Math.Sqrt(1 - value));
    }
}

/// <summary>Verifies a particular immutable provider configuration version.</summary>
public interface IRoutingProviderVerifier
{
    /// <summary>Runs bounded probes and persists verification only if concurrency state remains current.</summary>
    Task<RoutingVerificationResult> VerifyAsync(Guid providerId, int expectedVersion, uint expectedRowVersion, CancellationToken cancellationToken);
}

/// <summary>Contains the bounded verification outcome.</summary>
public sealed record RoutingVerificationResult(bool Succeeded, string? ErrorCode, int? VerifiedVersion = null, uint? RowVersion = null)
{
    /// <summary>Creates a bounded failure.</summary>
    public static RoutingVerificationResult Failure(string code) => new(false, code);
}
