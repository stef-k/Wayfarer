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
    private readonly RoutingProviderCredentialService _credentials;
    private readonly RoutingAttemptCoordinator _attempts;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes bounded verification.</summary>
    public RoutingProviderVerifier(
        ApplicationDbContext dbContext, RoutingBoundedExecutor executor, RoutingProviderCredentialService credentials,
        RoutingAttemptCoordinator attempts, TimeProvider? timeProvider = null)
        => (_dbContext, _executor, _credentials, _attempts, _timeProvider)
            = (dbContext, executor, credentials, attempts, timeProvider ?? TimeProvider.System);

    /// <inheritdoc />
    public async Task<RoutingVerificationResult> VerifyAsync(
        Guid providerId, int expectedVersion, uint expectedRowVersion, string administratorId, CancellationToken cancellationToken)
    {
        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var operationTimer = _timeProvider.CreateTimer(
            _ => operationTimeout.Cancel(), null, TimeSpan.FromSeconds(600), Timeout.InfiniteTimeSpan);
        try
        {
            var result = await VerifyCoreAsync(
                providerId, expectedVersion, expectedRowVersion, administratorId, operationTimeout.Token);
            return operationTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                ? RoutingVerificationResult.Failure("routing-timeout") : result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return RoutingVerificationResult.Failure("routing-timeout"); }
        catch (OperationCanceledException)
        { return RoutingVerificationResult.Failure("request-cancelled"); }
    }

    private async Task<RoutingVerificationResult> VerifyCoreAsync(
        Guid providerId, int expectedVersion, uint expectedRowVersion, string administratorId,
        CancellationToken operationToken)
    {
        var provider = await _dbContext.Set<RoutingProviderConfiguration>().AsNoTracking()
            .Include(item => item.ProfileMappings).ThenInclude(item => item.TransportProfile)
            .SingleOrDefaultAsync(item => item.Id == providerId, operationToken);
        if (provider == null || provider.ConfigurationVersion != expectedVersion || provider.RowVersion != expectedRowVersion
            || RoutingProviderStateResolver.Resolve(provider, false) is RoutingProviderState.Incomplete or RoutingProviderState.Invalid)
            return await FailureAsync(providerId, administratorId, "provider-configuration-stale", operationToken);
        var profiles = provider.ProfileMappings.Select(item => item.OsrmProfile).Distinct(StringComparer.Ordinal).ToArray();
        if (profiles.Length is 0 or > MaximumProfiles) return await FailureAsync(providerId, administratorId, "provider-profile-count-invalid", operationToken);
        var credential = _credentials.Read(provider);
        if (!credential.Succeeded) return await FailureAsync(providerId, administratorId, credential.ErrorCode!, operationToken);
        if (provider.CredentialRequired && string.IsNullOrEmpty(credential.Credential))
            return await FailureAsync(providerId, administratorId, "provider-credential-required", operationToken);
        var from = new RouteCoordinate(provider.VerificationFromLongitude!.Value, provider.VerificationFromLatitude!.Value);
        var to = new RouteCoordinate(provider.VerificationToLongitude!.Value, provider.VerificationToLatitude!.Value);
        foreach (var profile in profiles)
        {
            var request = OsrmRoutingAdapter.BuildRelativeRequest(profile, [from, to]);
            var execution = await _executor.GetJsonAsync(new Uri(provider.BaseEndpoint!), request,
                provider.ResponseSizeLimitBytes, TimeSpan.FromSeconds(5), operationToken, credential.Credential,
                prepareAttempt: token => _attempts.PrepareAsync(provider,
                    inner => IsCurrentForVerificationAsync(providerId, expectedVersion, profile, inner), token));
            if (!execution.Succeeded)
            {
                var category = execution.ErrorCode!;
                return await FailureAsync(providerId, administratorId, category,
                    category == "request-cancelled" ? CancellationToken.None : operationToken);
            }
            using var response = JsonResponse(execution.Json!);
            var route = await OsrmRoutingAdapter.ParseAsync(response, operationToken);
            if (!route.Succeeded || route.Waypoints.Count != 2 || route.Geometry.Count > 1000
                || DistanceMetres(from, route.Waypoints[0]) > MaximumAnchorDeviationMetres
                || DistanceMetres(to, route.Waypoints[1]) > MaximumAnchorDeviationMetres)
                return await FailureAsync(providerId, administratorId, "provider-verification-invalid", operationToken);
        }

        var tracked = await _dbContext.Set<RoutingProviderConfiguration>().SingleAsync(item => item.Id == providerId, operationToken);
        if (tracked.ConfigurationVersion != expectedVersion || tracked.RowVersion != expectedRowVersion)
            return await FailureAsync(providerId, administratorId, "provider-configuration-stale", operationToken);
        tracked.VerifiedConfigurationVersion = tracked.ConfigurationVersion;
        tracked.VerificationStatus = "verified";
        tracked.VerificationResult = "All mapped profiles returned valid bounded routes.";
        AddAudit(administratorId, providerId, "success", "ready-to-verified");
        try { await _dbContext.SaveChangesAsync(operationToken); }
        catch (DbUpdateConcurrencyException) { return RoutingVerificationResult.Failure("provider-configuration-stale"); }
        return new RoutingVerificationResult(true, null, tracked.ConfigurationVersion, tracked.RowVersion);
    }

    private async Task<bool> IsCurrentForVerificationAsync(
        Guid providerId, int expectedVersion, string profile, CancellationToken cancellationToken)
    {
        var current = await _dbContext.Set<RoutingProviderConfiguration>().AsNoTracking()
            .Include(item => item.ProfileMappings).ThenInclude(item => item.TransportProfile)
            .SingleOrDefaultAsync(item => item.Id == providerId, cancellationToken);
        return current is { Enabled: true } && current.ConfigurationVersion == expectedVersion
            && current.ProfileMappings.Any(mapping => mapping.OsrmProfile == profile
                && mapping.TransportProfile is { IsActive: true });
    }

    private async Task<RoutingVerificationResult> FailureAsync(
        Guid providerId, string administratorId, string category, CancellationToken cancellationToken)
    {
        AddAudit(administratorId, providerId, category, "verification-failed");
        await _dbContext.SaveChangesAsync(cancellationToken);
        return RoutingVerificationResult.Failure(category);
    }

    private void AddAudit(string administratorId, Guid providerId, string category, string transition) =>
        _dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = administratorId, Action = "RoutingProviderVerification", Timestamp = DateTime.UtcNow,
            Details = $"ProviderId={providerId}; AdapterType=OsrmCompatible; Category={category}; Transition={transition}."
        });

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
    Task<RoutingVerificationResult> VerifyAsync(
        Guid providerId, int expectedVersion, uint expectedRowVersion, string administratorId, CancellationToken cancellationToken);
}

/// <summary>Contains the bounded verification outcome.</summary>
public sealed record RoutingVerificationResult(bool Succeeded, string? ErrorCode, int? VerifiedVersion = null, uint? RowVersion = null)
{
    /// <summary>Creates a bounded failure.</summary>
    public static RoutingVerificationResult Failure(string code) => new(false, code);
}
