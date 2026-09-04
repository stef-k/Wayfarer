using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Loads additive Mobile mode discovery from current personal-provider authority.</summary>
public sealed class MobileRoutingProfileDiscoveryService
{
    private readonly ApplicationDbContext dbContext;
    private readonly PersonalProviderCredentialService personalCredentials;
    private readonly ILogger<MobileRoutingProfileDiscoveryService>? logger;

    /// <summary>Initializes provider-native discovery without legacy routing dependencies.</summary>
    public MobileRoutingProfileDiscoveryService(ApplicationDbContext dbContext,
        PersonalProviderCredentialService personalCredentials,
        ILogger<MobileRoutingProfileDiscoveryService>? logger = null) =>
        (this.dbContext, this.personalCredentials, this.logger) = (dbContext, personalCredentials, logger);

    /// <summary>Provides a controlled seam for authority-drift tests after the single protected read.</summary>
    internal Func<CancellationToken, Task> AfterCredentialReadAsync { get; set; } = _ => Task.CompletedTask;
    /// <summary>Provides a controlled counter seam for protected-readability tests.</summary>
    internal Func<bool>? CredentialReadOverride { get; set; }

    /// <summary>Discovers current modes and legacy exact-key choices without contact or admission.</summary>
    public async Task<MobileRoutingProfileDiscovery> DiscoverAsync(string userId, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await LoadAsync(userId, cancellationToken);
            if (snapshot == null) return MobileRoutingProfileDiscovery.Failure("no-authority");
            var readable = CredentialReadOverride?.Invoke() ?? personalCredentials.Read(snapshot.Profile).Succeeded;
            if (!readable) return MobileRoutingProfileDiscovery.Failure("authority-unavailable");
            await AfterCredentialReadAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            var current = await LoadAsync(userId, cancellationToken);
            if (current == null || snapshot.Stamp != current.Stamp)
                return MobileRoutingProfileDiscovery.Failure("temporarily-unavailable");
            var modes = ProviderDirectionsCatalog.For("geoapify");
            var identity = DiscoveryCatalogIdentity.Compute(
                new MobileRoutingDiscoveryCatalogProjection("available", snapshot.Profiles, modes));
            var currentIdentity = DiscoveryCatalogIdentity.Compute(
                new MobileRoutingDiscoveryCatalogProjection("available", current.Profiles, modes));
            return identity == currentIdentity
                ? new("available", identity, snapshot.Profiles, "geoapify", modes)
                : MobileRoutingProfileDiscovery.Failure("temporarily-unavailable");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            logger?.LogWarning(new EventId(53801, "MobileProviderModeDiscoveryUnavailable"),
                "Mobile provider-mode discovery failed locally.");
            return MobileRoutingProfileDiscovery.Failure("temporarily-unavailable");
        }
    }

    private async Task<DiscoverySnapshot?> LoadAsync(string userId, CancellationToken cancellationToken)
    {
        var selection = await dbContext.Set<PersonalLocationProviderSelection>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (selection?.RoutingProviderKey != "geoapify") return null;
        var profile = await dbContext.Set<PersonalLocationProviderProfile>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.ProviderKey == "geoapify", cancellationToken);
        if (profile == null || profile.RevokedAt != null || !profile.RoutingAuthorized
            || profile.RoutingVerification != PersonalProviderVerification.Verified
            || profile.RoutingVerifiedCredentialGeneration != profile.CredentialGeneration
            || profile.RoutingVerifiedConfigurationGeneration != profile.RoutingGeneration) return null;
        var legacyProfiles = (await EligibleQuery(Guid.Empty).ToArrayAsync(cancellationToken))
            .Select(item => new MobileRoutingProfile(
                item.TransportProfileId, item.DisplayName, item.ModeKey, item.Category)).ToArray();
        if (legacyProfiles.Length > 100) return null;
        return new(profile, legacyProfiles, new(selection.RoutingSelectionGeneration, profile.Id,
            profile.CredentialGeneration, profile.RoutingGeneration, profile.RoutingVerification,
            profile.RoutingVerifiedCredentialGeneration, profile.RoutingVerifiedConfigurationGeneration));
    }

    /// <summary>Builds the bounded released-client exact-key compatibility projection.</summary>
    internal IQueryable<MobileRoutingDiscoveryProfile> EligibleQuery(Guid ignoredProviderId) =>
        dbContext.Set<TransportProfile>().AsNoTracking()
            .Where(item => item.IsActive && (item.Key == "walk" || item.Key == "bicycle" || item.Key == "bike"
                || item.Key == "car" || item.Key == "bus"))
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Label).ThenBy(item => item.Key).ThenBy(item => item.Id)
            .Select(item => new MobileRoutingDiscoveryProfile(
                item.Id, item.Label, item.Key, item.Category, item.SortOrder)).Take(101);

    private sealed record DiscoverySnapshot(PersonalLocationProviderProfile Profile,
        IReadOnlyList<MobileRoutingProfile> Profiles, DiscoveryAuthorityStamp Stamp);
    private sealed record DiscoveryAuthorityStamp(int SelectionGeneration, Guid ProfileId, int CredentialGeneration,
        int RoutingGeneration, PersonalProviderVerification Verification, int? VerifiedCredentialGeneration,
        int? VerifiedRoutingGeneration);
}

/// <summary>Contains additive provider-native discovery plus released-client profile choices.</summary>
public sealed record MobileRoutingProfileDiscovery(string Outcome, string? DiscoveryCatalogIdentity,
    IReadOnlyList<MobileRoutingProfile> Profiles, string? Provider = null,
    IReadOnlyList<ProviderDirectionsMode>? ProviderModes = null)
{
    /// <summary>Gets the additive closed provider-native mode catalog.</summary>
    public IReadOnlyList<ProviderDirectionsMode> Modes => ProviderModes ?? [];
    /// <summary>Creates a non-available result with no partial authority.</summary>
    public static MobileRoutingProfileDiscovery Failure(string outcome) => new(outcome, null, []);
}

/// <summary>Contains one legacy released-client stable Transport Profile choice.</summary>
public sealed record MobileRoutingProfile(Guid TransportProfileId, string DisplayName, string ModeKey, string Category);

/// <summary>Contains the internal bounded released-client profile projection.</summary>
internal sealed record MobileRoutingDiscoveryProfile(Guid TransportProfileId, string DisplayName,
    string ModeKey, string Category, int SortOrder);
