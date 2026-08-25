using System.Data;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationEnrichment;

namespace Wayfarer.Services.LocationProviders;

/// <summary>Reads one bounded, credential-free persistent-geocoding status snapshot.</summary>
public interface IPersonalProviderStatusReader
{
    Task<PersonalProviderInspection> InspectPersistentGeocodingAsync(
        string userId, CancellationToken cancellationToken = default);
}

/// <summary>Projects provider authority and metering from one short consistent relational snapshot.</summary>
public sealed class PersonalProviderStatusReader(
    IDbContextFactory<ApplicationDbContext> contexts,
    PersonalProviderCredentialService credentials, IConfiguration configuration)
    : IPersonalProviderStatusReader
{
    /// <inheritdoc />
    public async Task<PersonalProviderInspection> InspectPersistentGeocodingAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken) : null;
        var dbNow = await LocationEnrichmentExecutionAuthority.DatabaseUtcNowAsync(db, cancellationToken);
        var selection = await db.PersonalLocationProviderSelections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        var providerKey = selection?.GeocodingProviderKey;
        var profile = providerKey is null ? null : await db.PersonalLocationProviderProfiles.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.ProviderKey == providerKey, cancellationToken);
        var authority = Resolve(selection, profile, providerKey);
        var status = providerKey switch
        {
            "geoapify" => await ReadGeoapifyAsync(db, userId, dbNow, cancellationToken),
            "mapbox" => await ReadMapboxAsync(db, userId, dbNow, cancellationToken),
            _ => UsageResult.Empty
        };
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return new(authority.Category, providerKey, status.GuardEnabled, status.Exhausted,
            status.NextAvailableAt, status.Usage, authority.Binding, dbNow);
    }

    private AuthorityResult Resolve(PersonalLocationProviderSelection? selection,
        PersonalLocationProviderProfile? profile, string? providerKey)
    {
        if (providerKey is null) return AuthorityResult.Fail(PersonalProviderAdmissionCategory.NoProviderSelected);
        if (providerKey is not ("geoapify" or "mapbox"))
            return AuthorityResult.Fail(PersonalProviderAdmissionCategory.UnsupportedProvider);
        if (profile is null || profile.RevokedAt != null || !profile.GeocodingAuthorized)
            return AuthorityResult.Fail(PersonalProviderAdmissionCategory.Unauthorized);
        if (!credentials.Read(profile).Succeeded)
            return AuthorityResult.Fail(PersonalProviderAdmissionCategory.CredentialUnavailable);
        if (providerKey == "mapbox" && !profile.HasCurrentPermanentGeocodingConsent())
            return AuthorityResult.Fail(PersonalProviderAdmissionCategory.ConsentRequired);
        var verified = profile.GeocodingVerification == PersonalProviderVerification.Verified
            && profile.GeocodingVerifiedCredentialGeneration == profile.CredentialGeneration
            && profile.GeocodingVerifiedConfigurationGeneration == profile.GeocodingGeneration;
        if (!verified) return AuthorityResult.Fail(PersonalProviderAdmissionCategory.Unverified);
        return new(PersonalProviderAdmissionCategory.Admitted, new(providerKey, profile.Id,
            profile.CredentialGeneration, profile.GeocodingGeneration, selection!.GeocodingSelectionGeneration,
            profile.GeocodingVerification, profile.GeocodingVerifiedCredentialGeneration,
            profile.GeocodingVerifiedConfigurationGeneration, profile.PermanentGeocodingConsentVersion,
            profile.PermanentGeocodingConsentedAt, profile.PermanentGeocodingConsentCredentialGeneration));
    }

    private async Task<UsageResult> ReadGeoapifyAsync(
        ApplicationDbContext db, string userId, DateTime dbNow, CancellationToken cancellationToken)
    {
        var guard = await db.GeoapifyUsageGuards.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        var limit = guard?.CreditLimit ?? configuration.GetValue("LocationProviders:Geoapify:RollingCreditLimit", 2500);
        var enabled = guard?.Enabled ?? true;
        var cutoff = new DateTimeOffset(dbNow.AddHours(-24), TimeSpan.Zero);
        var counted = db.GeoapifyUsageAdmissions.AsNoTracking()
            .Where(item => item.UserId == userId && item.AdmittedAt > cutoff);
        var used = await counted.SumAsync(item => (int?)item.Credits, cancellationToken) ?? 0;
        var oldest = await counted.MinAsync(item => (DateTimeOffset?)item.AdmittedAt, cancellationToken);
        var exhausted = enabled && used >= limit;
        return new(enabled, exhausted,
            exhausted && oldest.HasValue ? oldest.Value.AddHours(24).AddSeconds(5) : null,
            new(used, limit, "credits", cutoff, null));
    }

    private async Task<UsageResult> ReadMapboxAsync(
        ApplicationDbContext db, string userId, DateTime dbNow, CancellationToken cancellationToken)
    {
        var meter = await db.MapboxProductMeters.AsNoTracking().SingleOrDefaultAsync(item =>
            item.UserId == userId && item.Product == PersonalProviderProduct.PermanentGeocoding, cancellationToken);
        var limit = meter?.Limit ?? configuration.GetValue("LocationProviders:Mapbox:PermanentGeocodingLimit", 1000);
        var enabled = meter?.Enabled ?? true;
        var cycle = new DateOnly(dbNow.Year, dbNow.Month, 1);
        var used = meter?.CycleStart == cycle ? meter.AdmittedCount : 0;
        var exhausted = enabled && used >= limit;
        return new(enabled, exhausted,
            exhausted ? new DateTimeOffset(cycle.AddMonths(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
            new(used, limit, "contacts", null, cycle));
    }

    private sealed record AuthorityResult(
        PersonalProviderAdmissionCategory Category, PersonalProviderAuthorityBinding? Binding)
    {
        public static AuthorityResult Fail(PersonalProviderAdmissionCategory category) => new(category, null);
    }

    private sealed record UsageResult(bool GuardEnabled, bool Exhausted,
        DateTimeOffset? NextAvailableAt, PersonalProviderUsageStatus? Usage)
    {
        public static UsageResult Empty { get; } = new(false, false, null, null);
    }
}
