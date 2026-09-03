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
    public Task<MobileRoutingCapability> CapabilityAsync(string userId, Guid transportProfileId,
        CancellationToken cancellationToken) => CapabilityAsync(userId, transportProfileId, null, cancellationToken);

    /// <summary>Confirms a discovery choice and returns its selected executable authority.</summary>
    public async Task<MobileRoutingCapability> CapabilityAsync(string userId, Guid transportProfileId,
        string? discoveryCatalogIdentity, CancellationToken cancellationToken) =>
        await CapabilityAsync(userId, transportProfileId, null, discoveryCatalogIdentity, cancellationToken);

    /// <summary>Confirms either an explicit provider mode or the released-client compatibility choice.</summary>
    public async Task<MobileRoutingCapability> CapabilityAsync(string userId, Guid transportProfileId,
        string? providerMode, string? discoveryCatalogIdentity, CancellationToken cancellationToken)
    {
        if (discoveryCatalogIdentity is not null)
        {
            var catalog = await discovery.DiscoverAsync(userId, cancellationToken);
            if (catalog.Outcome != "available" || catalog.DiscoveryCatalogIdentity != discoveryCatalogIdentity
                || (providerMode == null
                    ? catalog.Profiles.All(item => item.TransportProfileId != transportProfileId)
                    : catalog.Modes.All(item => item.Key != providerMode)))
                return ChangedCatalogCapability(transportProfileId);
        }
        var resolution = await ResolveAsync(userId, transportProfileId, providerMode, cancellationToken);
        if (resolution.Execution == null)
            return new(MapOutcome(resolution.ErrorCode), transportProfileId, null, null, null, null, null,
                discoveryCatalogIdentity, null);
        var execution = resolution.Execution;
        var selectedIdentity = ComputeSelectedIdentity(userId, transportProfileId, execution);
        await AfterCapabilityResolutionAsync(cancellationToken);
        if (!MobileRoutingExecutionEligibility.IsSupported(execution, userId)
            || !await SelectedAuthorityCurrentAsync(userId, transportProfileId, providerMode, selectedIdentity, cancellationToken))
            return UnavailableCapability(transportProfileId);
        var guard = await dbContext.GeoapifyUsageGuards.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        var used = await dbContext.GeoapifyUsageAdmissions.AsNoTracking()
            .Where(item => item.UserId == userId && item.AdmittedAt > cutoff)
            .SumAsync(item => (int?)item.Credits, cancellationToken) ?? 0;
        if (guard is { Enabled: true } && used >= guard.CreditLimit)
            return new("exhausted", transportProfileId, null, null, null, null, null,
                discoveryCatalogIdentity, selectedIdentity);
        return new("available", transportProfileId, "geoapify", execution.Provider.Id,
            MappingIdentity(execution, transportProfileId), "persistent", Attributions(),
            discoveryCatalogIdentity, selectedIdentity, execution.Profile);
    }

    /// <summary>Generates one validated provider-neutral route and never mutates Trip Editor or domain state.</summary>
    public Task<MobileRouteServiceResult> RouteAsync(string userId, Guid transportProfileId,
        IReadOnlyList<RouteCoordinate> points, CancellationToken cancellationToken) =>
        RouteAsync(userId, transportProfileId, points, null, cancellationToken);

    /// <summary>Generates one route with an optional pre-admission discovery authority fence.</summary>
    public async Task<MobileRouteServiceResult> RouteAsync(string userId, Guid transportProfileId,
        IReadOnlyList<RouteCoordinate> points, string? selectedProfileAuthorityIdentity, CancellationToken cancellationToken)
        => await RouteAsync(userId, transportProfileId, points, null,
            selectedProfileAuthorityIdentity, cancellationToken);

    /// <summary>Generates one route for an additive explicit mode or the released-client compatibility path.</summary>
    public async Task<MobileRouteServiceResult> RouteAsync(string userId, Guid transportProfileId,
        IReadOnlyList<RouteCoordinate> points, string? providerMode,
        string? selectedProfileAuthorityIdentity, CancellationToken cancellationToken)
    {
        if (points.Count is < 2 or > 5 || points.Any(point => !point.IsValid)
            || points.Zip(points.Skip(1), (first, second) => first == second).Any(equal => equal))
            return MobileRouteServiceResult.Failure("invalid-request");
        var resolution = await ResolveAsync(userId, transportProfileId, providerMode, cancellationToken);
        if (resolution.Execution == null) return MobileRouteServiceResult.Failure(MapOutcome(resolution.ErrorCode));
        if (!MobileRoutingExecutionEligibility.IsSupported(resolution.Execution, userId))
            return MobileRouteServiceResult.Failure("no-provider-selected");
        var execution = resolution.Execution;
        if (selectedProfileAuthorityIdentity is not null
            && ComputeSelectedIdentity(userId, transportProfileId, execution) != selectedProfileAuthorityIdentity)
            return MobileRouteServiceResult.Failure("authority-changed");
        await AfterRouteResolutionAsync(cancellationToken);
        if (selectedProfileAuthorityIdentity is not null && !await SelectedAuthorityCurrentAsync(
            userId, transportProfileId, providerMode, selectedProfileAuthorityIdentity, cancellationToken))
            return MobileRouteServiceResult.Failure("authority-changed");
        if (!budgets.TryAdmitUserGeneration(userId)) return MobileRouteServiceResult.Failure("rate-limited");
        if (selectedProfileAuthorityIdentity is not null && !await SelectedAuthorityCurrentAsync(
            userId, transportProfileId, providerMode, selectedProfileAuthorityIdentity, cancellationToken))
            return MobileRouteServiceResult.Failure("authority-changed");
        var route = await routeClient.RouteAsync(execution, points,
            token => CompleteAuthorityCurrentAsync(userId, transportProfileId, providerMode, execution,
                selectedProfileAuthorityIdentity, token), cancellationToken);
        if (!route.Succeeded)
            return MobileRouteServiceResult.Failure(selectedProfileAuthorityIdentity is not null
                && route.ErrorCode == "configuration-changed" ? "authority-changed" : MapOutcome(route.ErrorCode));
        var validated = geometryValidator.Validate(points, route, cancellationToken);
        if (!validated.Succeeded) return MobileRouteServiceResult.Failure("invalid-response");
        await BeforeRoutePublicationAsync(cancellationToken);
        if (!await CompleteAuthorityCurrentAsync(
            userId, transportProfileId, providerMode, execution, selectedProfileAuthorityIdentity, cancellationToken))
            return MobileRouteServiceResult.Failure(selectedProfileAuthorityIdentity is null ? "configuration-changed" : "authority-changed");
        return new(true, "available", validated.Geometry!, route.DistanceMetres, route.DurationSeconds,
            route.Instructions, clock.GetUtcNow(), "geoapify", execution.Provider.Id,
            MappingIdentity(execution, transportProfileId), transportProfileId, points,
            Attributions(), "persistent", selectedProfileAuthorityIdentity, execution.Profile);
    }

    private Task<RoutingProviderResolutionResult> ResolveAsync(string userId, Guid transportProfileId,
        string? providerMode, CancellationToken cancellationToken) => providerMode == null
        ? resolver.ResolveReleasedMobileAsync(userId, transportProfileId, cancellationToken)
        : resolver.ResolveNativeAsync(userId, providerMode, cancellationToken);

    private async Task<bool> AuthorityCurrentAsync(string userId, Guid profileId,
        string? providerMode, ResolvedRoutingProviderExecution expected, CancellationToken cancellationToken)
    {
        var current = (await ResolveAsync(userId, profileId, providerMode, cancellationToken)).Execution;
        return current != null && current.Provider.Id == expected.Provider.Id
            && current.ProviderConfigurationVersion == expected.ProviderConfigurationVersion
            && current.Profile == expected.Profile && current.UserConfigurationVersion == expected.UserConfigurationVersion
            && current.UserRowVersion == expected.UserRowVersion;
    }

    private async Task<bool> CompleteAuthorityCurrentAsync(string userId, Guid profileId,
        string? providerMode, ResolvedRoutingProviderExecution expected, string? selectedProfileAuthorityIdentity,
        CancellationToken cancellationToken) =>
        await AuthorityCurrentAsync(userId, profileId, providerMode, expected, cancellationToken)
        && (selectedProfileAuthorityIdentity is null || await SelectedAuthorityCurrentAsync(
            userId, profileId, providerMode, selectedProfileAuthorityIdentity, cancellationToken));

    private async Task<bool> SelectedAuthorityCurrentAsync(string userId, Guid profileId,
        string? providerMode, string expectedIdentity, CancellationToken cancellationToken)
    {
        var current = (await ResolveAsync(userId, profileId, providerMode, cancellationToken)).Execution;
        return current != null && MobileRoutingExecutionEligibility.IsSupported(current, userId)
            && ComputeSelectedIdentity(userId, profileId, current) == expectedIdentity;
    }

    private static string ComputeSelectedIdentity(string userId, Guid profileId,
        ResolvedRoutingProviderExecution execution) => SelectedProfileAuthorityIdentity.Compute(new(
            userId, execution.FeatureStateGeneration, (int)execution.SelectionMode, execution.Provider.Id,
            (int)execution.Provider.AdapterType, execution.Provider.Enabled, execution.ProviderConfigurationVersion,
            execution.ProviderRowVersion, execution.UserConfigurationVersion, execution.UserRowVersion,
            profileId, execution.Profile, !string.IsNullOrEmpty(execution.Credential),
            execution.AuthoritySelectionGeneration, execution.RoutingAuthorized, execution.RoutingVerification,
            execution.VerifiedCredentialGeneration, execution.VerifiedRoutingGeneration,
            execution.ProviderVerifiedConfigurationVersion));

    private static string MappingIdentity(ResolvedRoutingProviderExecution execution, Guid profileId) =>
        $"{execution.Provider.Id:N}:{execution.ProviderConfigurationVersion}:{profileId:N}";

    private static MobileRoutingCapability UnavailableCapability(Guid profileId) =>
        new("no-provider-selected", profileId, null, null, null, null, null, null, null);
    private static MobileRoutingCapability ChangedCatalogCapability(Guid profileId) =>
        new("catalog-changed", profileId, null, null, null, null, null, null, null);

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
    IReadOnlyList<MobileRouteAttribution>? Attribution, string? DiscoveryCatalogIdentity,
    string? SelectedProfileAuthorityIdentity, string? ProviderMode = null);

/// <summary>Contains one safe linked attribution entry.</summary>
public sealed record MobileRouteAttribution(string Text, string Url);

/// <summary>Contains one validated ad-hoc route or a bounded failure.</summary>
public sealed record MobileRouteServiceResult(bool Succeeded, string Outcome,
    IReadOnlyList<RouteCoordinate>? Geometry = null, double? DistanceMetres = null, double? DurationSeconds = null,
    IReadOnlyList<RouteInstruction>? Instructions = null, DateTimeOffset? GeneratedAt = null, string? Provider = null,
    Guid? ProviderConfigurationId = null, string? MappingIdentity = null, Guid? TransportProfileId = null,
    IReadOnlyList<RouteCoordinate>? MatchPoints = null, IReadOnlyList<MobileRouteAttribution>? Attribution = null,
    string? StorageMode = null, string? SelectedProfileAuthorityIdentity = null, string? ProviderMode = null)
{
    public static MobileRouteServiceResult Failure(string outcome) => new(false, outcome);
}
