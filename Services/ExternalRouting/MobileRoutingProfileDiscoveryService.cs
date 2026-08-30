using System.Data;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Loads one bounded, provider-neutral Mobile routing catalog without provider contact or admission.</summary>
public sealed class MobileRoutingProfileDiscoveryService(
    ApplicationDbContext dbContext, RoutingProviderCredentialService providerCredentials,
    UserRoutingCredentialService userCredentials, PersonalProviderCredentialService personalCredentials)
{
    /// <summary>Provides a controlled seam for authority-drift tests after the single protected read.</summary>
    internal Func<CancellationToken, Task> AfterCredentialReadAsync { get; set; } = _ => Task.CompletedTask;
    /// <summary>Provides a controlled counter seam for protected-readability tests.</summary>
    internal Func<bool>? CredentialReadOverride { get; set; }

    /// <summary>Discovers the complete current eligible profile set for an authenticated active user.</summary>
    public async Task<MobileRoutingProfileDiscovery> DiscoverAsync(string userId, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await LoadCoherentAsync(userId, cancellationToken);
            if (snapshot.Outcome != "available") return MobileRoutingProfileDiscovery.Failure(snapshot.Outcome);
            if (snapshot.Projection!.Profiles.Count > 100)
                return MobileRoutingProfileDiscovery.Failure("profile-limit-exceeded");

            var readable = ReadCredential(snapshot);
            if (!readable) return MobileRoutingProfileDiscovery.Failure("authority-unavailable");
            var projection = snapshot.Projection with { CredentialReadable = true };
            await AfterCredentialReadAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            var current = await LoadCoherentAsync(userId, cancellationToken);
            if (current.Outcome != "available" || current.Projection is null
                || current.Projection.Profiles.Count > 100
                || MobileRoutingAuthorityIdentity.Compute(projection)
                    != MobileRoutingAuthorityIdentity.Compute(current.Projection with { CredentialReadable = true }))
                return MobileRoutingProfileDiscovery.Failure("temporarily-unavailable");

            var profiles = projection.Profiles.Select(item => new MobileRoutingProfile(
                item.TransportProfileId, item.Label, item.Key, item.Category)).ToArray();
            return new("available", MobileRoutingAuthorityIdentity.Compute(projection), profiles);
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
        {
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            return MobileRoutingProfileDiscovery.Failure("temporarily-unavailable");
        }
    }

    private async Task<DiscoverySnapshot> LoadCoherentAsync(string userId, CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational()) return await LoadAsync(userId, cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        var snapshot = await LoadAsync(userId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    private async Task<DiscoverySnapshot> LoadAsync(string userId, CancellationToken cancellationToken)
    {
        var settings = await dbContext.ApplicationSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        if (settings?.ExternalRouteGenerationEnabled != true) return DiscoverySnapshot.Failure("routing-disabled");
        var selection = await dbContext.Set<PersonalLocationProviderSelection>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (selection?.RoutingProviderKey == "geoapify")
            return await LoadPersonalProviderAsync(userId, settings, selection, cancellationToken);
        return await LoadConfiguredProviderAsync(userId, settings, cancellationToken);
    }

    private async Task<DiscoverySnapshot> LoadPersonalProviderAsync(string userId, ApplicationSettings settings,
        PersonalLocationProviderSelection selection, CancellationToken cancellationToken)
    {
        var personal = await dbContext.Set<PersonalLocationProviderProfile>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.ProviderKey == "geoapify", cancellationToken);
        if (personal == null || personal.RevokedAt != null || !personal.RoutingAuthorized)
            return DiscoverySnapshot.Failure("no-authority");
        if (personal.RoutingVerification != PersonalProviderVerification.Verified
            || personal.RoutingVerifiedCredentialGeneration != personal.CredentialGeneration
            || personal.RoutingVerifiedConfigurationGeneration != personal.RoutingGeneration)
            return DiscoverySnapshot.Failure("authority-unavailable");
        var providers = await dbContext.Set<RoutingProviderConfiguration>().AsNoTracking()
            .Include(item => item.ProfileMappings).ThenInclude(item => item.TransportProfile)
            .Where(item => item.AdapterType == RoutingAdapterType.Geoapify && item.Enabled)
            .ToListAsync(cancellationToken);
        if (providers.Count != 1) return DiscoverySnapshot.Failure("temporarily-unavailable");
        var provider = providers[0];
        if (provider.VerifiedConfigurationVersion != provider.ConfigurationVersion)
            return DiscoverySnapshot.Failure("authority-unavailable");
        var profiles = Eligible(provider);
        if (profiles.Count == 0) return DiscoverySnapshot.Failure("no-eligible-profiles");
        return new("available", new(settings.ExternalRouteGenerationEnabled, settings.ExternalRouteGenerationVersion,
            0x01, personal.ProviderKey, selection.RoutingSelectionGeneration, personal.Id, personal.RoutingAuthorized,
            personal.RoutingGeneration, personal.CredentialGeneration, (int)personal.RoutingVerification,
            personal.RoutingVerifiedCredentialGeneration, personal.RoutingVerifiedConfigurationGeneration,
            null, null, null, null, null, null, false, provider.Id, provider.Enabled, (int)provider.AdapterType,
            provider.ConfigurationVersion, provider.VerifiedConfigurationVersion, (int)provider.PersonalRoutingAccess,
            profiles), provider, null, personal);
    }

    private async Task<DiscoverySnapshot> LoadConfiguredProviderAsync(string userId, ApplicationSettings settings,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.Set<UserRoutingConfiguration>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (user == null) return DiscoverySnapshot.Failure("no-authority");
        var personal = user.SelectedProviderConfigurationId.HasValue;
        var providerId = user.SelectedProviderConfigurationId ?? settings.ActiveRoutingProviderConfigurationId;
        if (!providerId.HasValue) return DiscoverySnapshot.Failure("no-authority");
        var provider = await dbContext.Set<RoutingProviderConfiguration>().AsNoTracking()
            .Include(item => item.ProfileMappings).ThenInclude(item => item.TransportProfile)
            .SingleOrDefaultAsync(item => item.Id == providerId, cancellationToken);
        if (provider == null || !provider.Enabled || provider.VerifiedConfigurationVersion != provider.ConfigurationVersion)
            return DiscoverySnapshot.Failure("authority-unavailable");
        if (personal && !PersonalRoutingEligibility.Evaluate(provider).Eligible)
            return DiscoverySnapshot.Failure("authority-unavailable");
        if (personal && provider.PersonalRoutingAccess == PersonalRoutingAccess.CredentialFree
            && (user.CredentialPresent || user.CredentialCiphertext != null || user.VerificationStatus != null
                || user.VerifiedUserConfigurationVersion != null || user.VerifiedProviderConfigurationVersion != null))
            return DiscoverySnapshot.Failure("authority-unavailable");
        if (personal && provider.PersonalRoutingAccess == PersonalRoutingAccess.CredentialRequired
            && (!user.CredentialPresent || user.VerifiedUserConfigurationVersion != user.ConfigurationVersion
                || user.VerifiedProviderConfigurationVersion != provider.ConfigurationVersion
                || user.VerificationStatus != "verified"))
            return DiscoverySnapshot.Failure("authority-unavailable");
        var profiles = Eligible(provider);
        if (profiles.Count == 0) return DiscoverySnapshot.Failure("no-eligible-profiles");
        return new("available", new(settings.ExternalRouteGenerationEnabled, settings.ExternalRouteGenerationVersion,
            0x02, null, null, null, null, null, null, null, null, null,
            user.SelectedProviderConfigurationId, user.ConfigurationVersion, user.CredentialPresent,
            user.VerifiedUserConfigurationVersion, user.VerifiedProviderConfigurationVersion, user.VerificationStatus,
            false, provider.Id, provider.Enabled, (int)provider.AdapterType, provider.ConfigurationVersion,
            provider.VerifiedConfigurationVersion, (int)provider.PersonalRoutingAccess, profiles), provider, user, null);
    }

    private bool ReadCredential(DiscoverySnapshot snapshot)
    {
        if (CredentialReadOverride is not null) return CredentialReadOverride();
        if (snapshot.PersonalProfile is not null) return personalCredentials.Read(snapshot.PersonalProfile).Succeeded;
        if (snapshot.UserConfiguration!.SelectedProviderConfigurationId.HasValue
            && snapshot.Provider!.PersonalRoutingAccess == PersonalRoutingAccess.CredentialRequired)
            return userCredentials.Unprotect(snapshot.UserConfiguration.UserId, snapshot.Provider.Id,
                snapshot.UserConfiguration.CredentialCiphertext).Succeeded;
        if (snapshot.UserConfiguration.SelectedProviderConfigurationId.HasValue) return true;
        return providerCredentials.Read(snapshot.Provider!).Succeeded;
    }

    private static IReadOnlyList<MobileRoutingAuthorityProfile> Eligible(RoutingProviderConfiguration provider) =>
        provider.ProfileMappings.Where(item => item.TransportProfile is { IsActive: true })
            .Where(item => ProviderTransportProfileResolver.Resolve(provider, item.TransportProfile).Category
                == ProviderTransportProfileCategory.Supported)
            .Select(item => new MobileRoutingAuthorityProfile(item.TransportProfileId, true,
                item.TransportProfile.SortOrder, item.TransportProfile.Label, item.TransportProfile.Key,
                item.TransportProfile.Category))
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Label, StringComparer.Ordinal)
            .ThenBy(item => item.Key, StringComparer.Ordinal).ThenBy(item => item.TransportProfileId, GuidNetworkComparer.Instance)
            .Take(101).ToArray();

    private sealed record DiscoverySnapshot(string Outcome, MobileRoutingAuthorityProjection? Projection,
        RoutingProviderConfiguration? Provider, UserRoutingConfiguration? UserConfiguration,
        PersonalLocationProviderProfile? PersonalProfile)
    {
        public static DiscoverySnapshot Failure(string outcome) => new(outcome, null, null, null, null);
    }
}

/// <summary>Contains a bounded discovery outcome and complete provider-neutral choices.</summary>
public sealed record MobileRoutingProfileDiscovery(string Outcome, string? AuthorityIdentity,
    IReadOnlyList<MobileRoutingProfile> Profiles)
{
    /// <summary>Creates a non-available result with no partial authority.</summary>
    public static MobileRoutingProfileDiscovery Failure(string outcome) => new(outcome, null, []);
}

/// <summary>Contains one provider-neutral eligible transport profile choice.</summary>
public sealed record MobileRoutingProfile(Guid TransportProfileId, string DisplayName, string ModeKey, string Category);

/// <summary>Orders GUIDs by RFC-4122 network bytes.</summary>
internal sealed class GuidNetworkComparer : IComparer<Guid>
{
    public static GuidNetworkComparer Instance { get; } = new();
    public int Compare(Guid x, Guid y) { Span<byte> xb = stackalloc byte[16]; Span<byte> yb = stackalloc byte[16]; x.TryWriteBytes(xb, true, out _); y.TryWriteBytes(yb, true, out _); return xb.SequenceCompareTo(yb); }
}
