using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Owns provider-neutral mobile capability and ad-hoc route orchestration without persistence.</summary>
public sealed class MobileRoutingService(
    ApplicationDbContext dbContext, AuthoritativeRoutingProviderResolver resolver, IOsrmRouteClient routeClient,
    IProviderRouteGeometryValidator geometryValidator, RoutingRequestBudget budgets,
    MobileRoutingProfileDiscoveryService discovery, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    /// <summary>Provides a deterministic seam for coherent capability snapshot tests.</summary>
    internal Func<CancellationToken, Task> AfterCapabilityResolutionAsync { get; set; } = _ => Task.CompletedTask;
    /// <summary>Provides a deterministic seam for pre-admission authority-drift tests.</summary>
    internal Func<CancellationToken, Task> AfterRouteResolutionAsync { get; set; } = _ => Task.CompletedTask;
    /// <summary>Provides a deterministic seam for final-publication authority-drift tests.</summary>
    internal Func<CancellationToken, Task> BeforeRoutePublicationAsync { get; set; } = _ => Task.CompletedTask;

    /// <summary>Projects a no-contact capability for one stable Wayfarer transport profile.</summary>
    public async Task<MobileRoutingCapability> CapabilityAsync(
        string userId, Guid transportProfileId, CancellationToken cancellationToken)
    {
        var authority = await discovery.DiscoverAsync(userId, cancellationToken);
        if (authority.Outcome != "available" || authority.AuthorityIdentity is null
            || authority.Profiles.All(item => item.TransportProfileId != transportProfileId))
            return UnavailableCapability(transportProfileId, null);
        var resolution = await resolver.ResolveAsync(userId, transportProfileId, cancellationToken);
        if (resolution.Execution == null)
            return new(MapOutcome(resolution.ErrorCode), transportProfileId, null, null, null, null, null, null);
        var execution = resolution.Execution;
        await AfterCapabilityResolutionAsync(cancellationToken);
        if (!MobileRoutingExecutionEligibility.IsSupported(execution, userId)
            || !await discovery.IsAuthorityIdentityCurrentAsync(
                userId, transportProfileId, authority.AuthorityIdentity, cancellationToken))
            return UnavailableCapability(transportProfileId, null);
        var guard = await dbContext.GeoapifyUsageGuards.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        var used = await dbContext.GeoapifyUsageAdmissions.AsNoTracking()
            .Where(item => item.UserId == userId && item.AdmittedAt > cutoff)
            .SumAsync(item => (int?)item.Credits, cancellationToken) ?? 0;
        if (guard is { Enabled: true } && used >= guard.CreditLimit)
            return new("exhausted", transportProfileId, null, null, null, null, null, authority.AuthorityIdentity);
        return new("available", transportProfileId, "geoapify", execution.Provider.Id,
            MappingIdentity(execution, transportProfileId), "persistent", Attributions(), authority.AuthorityIdentity);
    }

    /// <summary>Generates one validated provider-neutral route and never mutates Trip Editor or domain state.</summary>
    public Task<MobileRouteServiceResult> RouteAsync(string userId, Guid transportProfileId,
        IReadOnlyList<RouteCoordinate> points, CancellationToken cancellationToken) =>
        RouteAsync(userId, transportProfileId, points, null, cancellationToken);

    /// <summary>Generates one route with an optional pre-admission discovery authority fence.</summary>
    public async Task<MobileRouteServiceResult> RouteAsync(string userId, Guid transportProfileId,
        IReadOnlyList<RouteCoordinate> points, string? authorityIdentity, CancellationToken cancellationToken)
    {
        if (points.Count is < 2 or > 5 || points.Any(point => !point.IsValid)
            || points.Zip(points.Skip(1), (first, second) => first == second).Any(equal => equal))
            return MobileRouteServiceResult.Failure("invalid-request");
        if (authorityIdentity is not null)
        {
            var authority = await discovery.DiscoverAsync(userId, cancellationToken);
            if (authority.Outcome != "available" || authority.AuthorityIdentity != authorityIdentity
                || authority.Profiles.All(item => item.TransportProfileId != transportProfileId))
                return MobileRouteServiceResult.Failure("authority-changed");
        }
        var resolution = await resolver.ResolveAsync(userId, transportProfileId, cancellationToken);
        if (resolution.Execution == null) return MobileRouteServiceResult.Failure(MapOutcome(resolution.ErrorCode));
        if (!MobileRoutingExecutionEligibility.IsSupported(resolution.Execution, userId))
            return MobileRouteServiceResult.Failure("no-provider-selected");
        await AfterRouteResolutionAsync(cancellationToken);
        if (authorityIdentity is not null && !await discovery.IsAuthorityIdentityCurrentAsync(
            userId, transportProfileId, authorityIdentity, cancellationToken))
            return MobileRouteServiceResult.Failure("authority-changed");
        if (!budgets.TryAdmitUserGeneration(userId)) return MobileRouteServiceResult.Failure("rate-limited");
        var execution = resolution.Execution;
        if (authorityIdentity is not null && !await discovery.IsAuthorityIdentityCurrentAsync(
            userId, transportProfileId, authorityIdentity, cancellationToken))
            return MobileRouteServiceResult.Failure("authority-changed");
        var route = await routeClient.RouteAsync(execution, points,
            token => CompleteAuthorityCurrentAsync(
                userId, transportProfileId, execution, authorityIdentity, token), cancellationToken);
        if (!route.Succeeded)
            return MobileRouteServiceResult.Failure(authorityIdentity is not null
                && route.ErrorCode == "configuration-changed" ? "authority-changed" : MapOutcome(route.ErrorCode));
        var validated = geometryValidator.Validate(points, route, cancellationToken);
        if (!validated.Succeeded) return MobileRouteServiceResult.Failure("invalid-response");
        await BeforeRoutePublicationAsync(cancellationToken);
        if (!await CompleteAuthorityCurrentAsync(
            userId, transportProfileId, execution, authorityIdentity, cancellationToken))
            return MobileRouteServiceResult.Failure(authorityIdentity is null ? "configuration-changed" : "authority-changed");
        return new(true, "available", validated.Geometry!, route.DistanceMetres, route.DurationSeconds,
            route.Instructions, clock.GetUtcNow(), "geoapify", execution.Provider.Id,
            MappingIdentity(execution, transportProfileId), transportProfileId, points,
            Attributions(), "persistent", authorityIdentity);
    }

    private async Task<bool> AuthorityCurrentAsync(string userId, Guid profileId,
        ResolvedRoutingProviderExecution expected, CancellationToken cancellationToken)
    {
        var current = (await resolver.ResolveAsync(userId, profileId, cancellationToken)).Execution;
        return current != null && current.Provider.Id == expected.Provider.Id
            && current.ProviderConfigurationVersion == expected.ProviderConfigurationVersion
            && current.Profile == expected.Profile && current.UserConfigurationVersion == expected.UserConfigurationVersion
            && current.UserRowVersion == expected.UserRowVersion;
    }

    private async Task<bool> CompleteAuthorityCurrentAsync(string userId, Guid profileId,
        ResolvedRoutingProviderExecution expected, string? authorityIdentity, CancellationToken cancellationToken) =>
        await AuthorityCurrentAsync(userId, profileId, expected, cancellationToken)
        && (authorityIdentity is null || await discovery.IsAuthorityIdentityCurrentAsync(
            userId, profileId, authorityIdentity, cancellationToken));

    private static string MappingIdentity(ResolvedRoutingProviderExecution execution, Guid profileId) =>
        $"{execution.Provider.Id:N}:{execution.ProviderConfigurationVersion}:{profileId:N}";

    private static MobileRoutingCapability UnavailableCapability(Guid profileId, string? authorityIdentity) =>
        new("no-provider-selected", profileId, null, null, null, null, null, authorityIdentity);

    private static IReadOnlyList<MobileRouteAttribution> Attributions() =>
    [
        new("Powered by Geoapify", "https://www.geoapify.com/"),
        new("© OpenStreetMap contributors", "https://www.openstreetmap.org/copyright")
    ];

    private static string MapOutcome(string? code) => code switch
    {
        "external-routing-disabled" or "personal-provider-unavailable" or "user-routing-unavailable" => "no-provider-selected",
        "unmapped-transport-profile" => "unmapped-transport-profile",
        "unsupported-transport-profile" => "unsupported-transport-profile",
        "unauthorized" => "unauthorized",
        "verification-required" => "verification-required",
        "routing-credit-exhausted" => "exhausted",
        "provider-rate-limited" or "routing-rate-limited" => "rate-limited",
        "provider-configuration-stale" => "configuration-changed",
        "provider-response-invalid" or "provider-route-invalid" => "invalid-response",
        "request-cancelled" => "cancelled",
        _ => "temporarily-unavailable"
    };
}

/// <summary>Contains no-contact mobile capability state and safe matching authority.</summary>
public sealed record MobileRoutingCapability(string Outcome, Guid TransportProfileId, string? Provider,
    Guid? ProviderConfigurationId, string? MappingIdentity, string? StorageMode,
    IReadOnlyList<MobileRouteAttribution>? Attribution, string? AuthorityIdentity);

/// <summary>Contains one safe linked attribution entry.</summary>
public sealed record MobileRouteAttribution(string Text, string Url);

/// <summary>Contains one validated ad-hoc route or a bounded failure.</summary>
public sealed record MobileRouteServiceResult(bool Succeeded, string Outcome,
    IReadOnlyList<RouteCoordinate>? Geometry = null, double? DistanceMetres = null, double? DurationSeconds = null,
    IReadOnlyList<RouteInstruction>? Instructions = null, DateTimeOffset? GeneratedAt = null, string? Provider = null,
    Guid? ProviderConfigurationId = null, string? MappingIdentity = null, Guid? TransportProfileId = null,
    IReadOnlyList<RouteCoordinate>? MatchPoints = null, IReadOnlyList<MobileRouteAttribution>? Attribution = null,
    string? StorageMode = null, string? AuthorityIdentity = null)
{
    public static MobileRouteServiceResult Failure(string outcome) => new(false, outcome);
}
