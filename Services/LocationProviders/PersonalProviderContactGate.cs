using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;

namespace Wayfarer.Services.LocationProviders;

/// <summary>Owns the lowest shared credential-authority and durable usage-admission seam before provider HTTP.</summary>
public sealed class PersonalProviderContactGate(
    ApplicationDbContext dbContext, PersonalProviderCredentialService credentials,
    LegacyMapboxMigrationService legacyMigration, IConfiguration configuration)
{
    /// <summary>Resolves current authority and durably admits the caller's validated provider-native cost.</summary>
    public async Task<PersonalProviderAdmission> AdmitAsync(
        string userId, PersonalProviderCapability capability, PersonalProviderProduct product,
        int cost, CancellationToken cancellationToken = default)
    {
        if (cost <= 0) return PersonalProviderAdmission.Rejected(PersonalProviderAdmissionCategory.InvalidCost);
        if (capability == PersonalProviderCapability.Geocoding)
            await legacyMigration.MigrateAsync(userId, cancellationToken);

        var authority = await ResolveAsync(userId, capability, cancellationToken);
        if (!authority.Succeeded) return PersonalProviderAdmission.Rejected(authority.Category);
        if (!ProductMatches(authority.ProviderKey!, capability, product))
            return PersonalProviderAdmission.Rejected(PersonalProviderAdmissionCategory.UnsupportedProduct);

        var admitted = authority.ProviderKey == "geoapify"
            ? await AdmitGeoapifyAsync(userId, product, cost, cancellationToken)
            : await AdmitMapboxAsync(userId, product, cost, cancellationToken);
        if (!admitted.Succeeded) return admitted;

        var snapshot = new PersonalProviderAuthoritySnapshot(userId, authority.ProviderKey!, capability,
            authority.Credential!, authority.CredentialGeneration, authority.CapabilityGeneration,
            authority.SelectionGeneration, authority.ConsentVersion, authority.ConsentCredentialGeneration);
        return new(PersonalProviderAdmissionCategory.Admitted, snapshot, admitted.Usage);
    }

    /// <summary>Admits explicit Permanent verification without requiring selection or prior verification.</summary>
    public async Task<PersonalProviderAdmission> AdmitMapboxPermanentVerificationAsync(string userId, CancellationToken cancellationToken = default)
    {
        await legacyMigration.MigrateAsync(userId, cancellationToken);
        var profile = await dbContext.Set<PersonalLocationProviderProfile>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.ProviderKey == "mapbox", cancellationToken);
        if (profile == null || profile.RevokedAt != null || !profile.GeocodingAuthorized)
            return PersonalProviderAdmission.Rejected(PersonalProviderAdmissionCategory.Unauthorized);
        if (!profile.HasCurrentPermanentGeocodingConsent())
            return PersonalProviderAdmission.Rejected(PersonalProviderAdmissionCategory.ConsentRequired);
        var read = credentials.Read(profile);
        if (!read.Succeeded) return PersonalProviderAdmission.Rejected(PersonalProviderAdmissionCategory.CredentialUnavailable);
        var admitted = await AdmitMapboxAsync(userId, PersonalProviderProduct.PermanentGeocoding, 1, cancellationToken);
        if (!admitted.Succeeded) return admitted;
        return new(PersonalProviderAdmissionCategory.Admitted,
            new(userId, "mapbox", PersonalProviderCapability.Geocoding, read.Credential!, profile.CredentialGeneration,
                profile.GeocodingGeneration, 0, profile.PermanentGeocodingConsentVersion,
                profile.PermanentGeocodingConsentCredentialGeneration), admitted.Usage);
    }

    /// <summary>Revalidates verification authority without imposing selection or verified state.</summary>
    public async Task<bool> IsVerificationCurrentAsync(PersonalProviderAuthoritySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var profile = await dbContext.Set<PersonalLocationProviderProfile>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == snapshot.UserId && item.ProviderKey == "mapbox", cancellationToken);
        return profile != null && profile.RevokedAt == null && profile.GeocodingAuthorized
            && profile.CredentialGeneration == snapshot.CredentialGeneration
            && profile.GeocodingGeneration == snapshot.CapabilityGeneration
            && profile.HasCurrentPermanentGeocodingConsent()
            && profile.PermanentGeocodingConsentVersion == snapshot.ConsentVersion
            && profile.PermanentGeocodingConsentCredentialGeneration == snapshot.ConsentCredentialGeneration;
    }

    /// <summary>Revalidates bounded authority immediately before contact and result persistence.</summary>
    public async Task<bool> IsCurrentAsync(
        PersonalProviderAuthoritySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var current = await ResolveAsync(snapshot.UserId, snapshot.Capability, cancellationToken);
        return current.Succeeded && current.ProviderKey == snapshot.ProviderKey
            && current.CredentialGeneration == snapshot.CredentialGeneration
            && current.CapabilityGeneration == snapshot.CapabilityGeneration
            && current.SelectionGeneration == snapshot.SelectionGeneration
            && current.ConsentVersion == snapshot.ConsentVersion
            && current.ConsentCredentialGeneration == snapshot.ConsentCredentialGeneration;
    }

    private async Task<ResolvedAuthority> ResolveAsync(
        string userId, PersonalProviderCapability capability, CancellationToken cancellationToken)
    {
        var selection = await dbContext.Set<PersonalLocationProviderSelection>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        var providerKey = capability == PersonalProviderCapability.Geocoding
            ? selection?.GeocodingProviderKey : selection?.RoutingProviderKey;
        if (providerKey == null) return ResolvedAuthority.Fail(PersonalProviderAdmissionCategory.NoProviderSelected);
        if (providerKey is not ("geoapify" or "mapbox"))
            return ResolvedAuthority.Fail(PersonalProviderAdmissionCategory.UnsupportedProvider);

        var profile = await dbContext.Set<PersonalLocationProviderProfile>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.ProviderKey == providerKey, cancellationToken);
        if (profile == null || profile.RevokedAt != null || !profile.IsAuthorized(capability))
            return ResolvedAuthority.Fail(PersonalProviderAdmissionCategory.Unauthorized);
        var read = credentials.Read(profile);
        if (!read.Succeeded) return ResolvedAuthority.Fail(PersonalProviderAdmissionCategory.CredentialUnavailable);

        var verified = capability == PersonalProviderCapability.Geocoding
            ? profile.GeocodingVerification == PersonalProviderVerification.Verified
              && profile.GeocodingVerifiedCredentialGeneration == profile.CredentialGeneration
              && profile.GeocodingVerifiedConfigurationGeneration == profile.GeocodingGeneration
            : profile.RoutingVerification == PersonalProviderVerification.Verified
              && profile.RoutingVerifiedCredentialGeneration == profile.CredentialGeneration
              && profile.RoutingVerifiedConfigurationGeneration == profile.RoutingGeneration;
        if (!verified) return ResolvedAuthority.Fail(PersonalProviderAdmissionCategory.Unverified);
        if (providerKey == "mapbox" && capability == PersonalProviderCapability.Geocoding
            && !profile.HasCurrentPermanentGeocodingConsent())
            return ResolvedAuthority.Fail(PersonalProviderAdmissionCategory.ConsentRequired);
        return new(true, PersonalProviderAdmissionCategory.Admitted, providerKey, read.Credential,
            profile.CredentialGeneration,
            capability == PersonalProviderCapability.Geocoding ? profile.GeocodingGeneration : profile.RoutingGeneration,
            capability == PersonalProviderCapability.Geocoding
                ? selection!.GeocodingSelectionGeneration : selection!.RoutingSelectionGeneration,
            profile.PermanentGeocodingConsentVersion, profile.PermanentGeocodingConsentCredentialGeneration);
    }

    private async Task<PersonalProviderAdmission> AdmitGeoapifyAsync(
        string userId, PersonalProviderProduct product, int credits, CancellationToken cancellationToken)
    {
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        var guard = await LockGeoapifyGuardAsync(userId, cancellationToken);
        var now = dbContext.Database.IsNpgsql()
            ? await dbContext.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken)
            : DateTimeOffset.UtcNow;
        var cutoff = now.AddHours(-24);
        var used = await dbContext.Set<GeoapifyUsageAdmission>()
            .Where(item => item.UserId == userId && item.AdmittedAt > cutoff).SumAsync(item => (int?)item.Credits, cancellationToken) ?? 0;
        if (guard.Enabled && (long)used + credits > guard.CreditLimit)
            return await CompleteAsync(PersonalProviderAdmission.Rejected(PersonalProviderAdmissionCategory.Exhausted,
                new(used, guard.CreditLimit, "credits", cutoff, null)), transaction, false, cancellationToken);

        dbContext.Set<GeoapifyUsageAdmission>().Add(new()
        { UserId = userId, Credits = credits, Product = product, AdmittedAt = now });
        await dbContext.Set<GeoapifyUsageAdmission>()
            .Where(item => item.UserId == userId && item.AdmittedAt <= cutoff).ExecuteDeleteAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await CompleteAsync(new(PersonalProviderAdmissionCategory.Admitted, null,
            new(used + credits, guard.CreditLimit, "credits", cutoff, null)), transaction, true, cancellationToken);
    }

    private async Task<GeoapifyUsageGuard> LockGeoapifyGuardAsync(string userId, CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsNpgsql())
        {
            var defaultLimit = configuration.GetValue("LocationProviders:Geoapify:RollingCreditLimit", 2500);
            await dbContext.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO "GeoapifyUsageGuards" ("UserId", "Enabled", "CreditLimit")
                VALUES ({{userId}}, TRUE, {{defaultLimit}}) ON CONFLICT ("UserId") DO NOTHING
                """, cancellationToken);
            return await dbContext.Set<GeoapifyUsageGuard>().FromSqlInterpolated($$"""
                SELECT *, xmin FROM "GeoapifyUsageGuards" WHERE "UserId" = {{userId}} FOR UPDATE
                """).SingleAsync(cancellationToken);
        }
        var guard = await dbContext.Set<GeoapifyUsageGuard>().SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (guard != null) return guard;
        guard = new() { UserId = userId };
        dbContext.Add(guard);
        await dbContext.SaveChangesAsync(cancellationToken);
        return guard;
    }

    private async Task<PersonalProviderAdmission> AdmitMapboxAsync(
        string userId, PersonalProviderProduct product, int cost, CancellationToken cancellationToken)
    {
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        var meter = await LockMapboxMeterAsync(userId, product, cancellationToken);
        var today = dbContext.Database.IsNpgsql()
            ? DateOnly.FromDateTime(await dbContext.Database.SqlQuery<DateTime>($"SELECT (clock_timestamp() AT TIME ZONE 'UTC') AS \"Value\"").SingleAsync(cancellationToken))
            : DateOnly.FromDateTime(DateTime.UtcNow);
        var cycle = new DateOnly(today.Year, today.Month, 1);
        if (meter.CycleStart != cycle) { meter.CycleStart = cycle; meter.AdmittedCount = 0; }
        if (meter.Enabled && (long)meter.AdmittedCount + cost > meter.Limit)
            return await CompleteAsync(PersonalProviderAdmission.Rejected(PersonalProviderAdmissionCategory.Exhausted,
                new(meter.AdmittedCount, meter.Limit, "contacts", null, cycle)), transaction, false, cancellationToken);
        meter.AdmittedCount = checked(meter.AdmittedCount + cost);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await CompleteAsync(new(PersonalProviderAdmissionCategory.Admitted, null,
            new(meter.AdmittedCount, meter.Limit, "contacts", null, cycle)), transaction, true, cancellationToken);
    }

    private async Task<MapboxProductMeter> LockMapboxMeterAsync(
        string userId, PersonalProviderProduct product, CancellationToken cancellationToken)
    {
        var key = product == PersonalProviderProduct.PermanentGeocoding ? "PermanentGeocodingLimit" : "DirectionsLimit";
        var limit = configuration.GetValue($"LocationProviders:Mapbox:{key}", 1000);
        if (dbContext.Database.IsNpgsql())
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO "MapboxProductMeters" ("UserId", "Product", "Enabled", "Limit", "CycleStart", "AdmittedCount")
                VALUES ({{userId}}, {{(int)product}}, TRUE, {{limit}}, DATE '1970-01-01', 0)
                ON CONFLICT ("UserId", "Product") DO NOTHING
                """, cancellationToken);
            return await dbContext.Set<MapboxProductMeter>().FromSqlInterpolated($$"""
                SELECT *, xmin FROM "MapboxProductMeters"
                WHERE "UserId" = {{userId}} AND "Product" = {{(int)product}} FOR UPDATE
                """).SingleAsync(cancellationToken);
        }
        var meter = await dbContext.Set<MapboxProductMeter>().SingleOrDefaultAsync(
            item => item.UserId == userId && item.Product == product, cancellationToken);
        if (meter != null) return meter;
        meter = new() { UserId = userId, Product = product, Limit = limit, CycleStart = new(1970, 1, 1) };
        dbContext.Add(meter); await dbContext.SaveChangesAsync(cancellationToken); return meter;
    }

    private static bool ProductMatches(string provider, PersonalProviderCapability capability, PersonalProviderProduct product) =>
        provider == "geoapify" && ((capability == PersonalProviderCapability.Geocoding && product == PersonalProviderProduct.Geocoding)
            || (capability == PersonalProviderCapability.Routing && product == PersonalProviderProduct.Routing))
        || provider == "mapbox" && ((capability == PersonalProviderCapability.Geocoding && product == PersonalProviderProduct.PermanentGeocoding)
            || (capability == PersonalProviderCapability.Routing && product == PersonalProviderProduct.Directions));

    private static async Task<PersonalProviderAdmission> CompleteAsync(
        PersonalProviderAdmission result, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction,
        bool commit, CancellationToken cancellationToken)
    {
        if (transaction != null)
        { if (commit) await transaction.CommitAsync(cancellationToken); else await transaction.RollbackAsync(cancellationToken); }
        return result;
    }

    private sealed record ResolvedAuthority(bool Succeeded, PersonalProviderAdmissionCategory Category,
        string? ProviderKey, string? Credential, int CredentialGeneration, int CapabilityGeneration, int SelectionGeneration,
        int? ConsentVersion = null, int? ConsentCredentialGeneration = null)
    {
        public static ResolvedAuthority Fail(PersonalProviderAdmissionCategory category) => new(false, category, null, null, 0, 0, 0);
    }
}

/// <summary>Identifies bounded admission outcomes safe for diagnostics.</summary>
public enum PersonalProviderAdmissionCategory
{ Admitted, InvalidCost, NoProviderSelected, UnsupportedProvider, UnsupportedProduct, Unauthorized, Unverified, ConsentRequired, CredentialUnavailable, Exhausted }

/// <summary>Contains server-internal immutable contact authority; it must never be serialized.</summary>
public sealed class PersonalProviderAuthoritySnapshot
{
    public PersonalProviderAuthoritySnapshot(string userId, string providerKey, PersonalProviderCapability capability,
        string credential, int credentialGeneration, int capabilityGeneration, int selectionGeneration,
        int? consentVersion = null, int? consentCredentialGeneration = null)
    {
        UserId = userId; ProviderKey = providerKey; Capability = capability; Credential = credential;
        CredentialGeneration = credentialGeneration; CapabilityGeneration = capabilityGeneration;
        SelectionGeneration = selectionGeneration;
        ConsentVersion = consentVersion; ConsentCredentialGeneration = consentCredentialGeneration;
    }
    public string UserId { get; }
    public string ProviderKey { get; }
    public PersonalProviderCapability Capability { get; }
    [JsonIgnore] public string Credential { get; }
    public int CredentialGeneration { get; }
    public int CapabilityGeneration { get; }
    public int SelectionGeneration { get; }
    public int? ConsentVersion { get; }
    public int? ConsentCredentialGeneration { get; }
    public override string ToString() => $"PersonalProviderAuthoritySnapshot {{ ProviderKey = {ProviderKey}, Capability = {Capability}, CredentialGeneration = {CredentialGeneration}, CapabilityGeneration = {CapabilityGeneration}, SelectionGeneration = {SelectionGeneration} }}";
}

/// <summary>Contains only bounded usage status.</summary>
public sealed record PersonalProviderUsageStatus(int Used, int Limit, string Unit, DateTimeOffset? RollingCutoff, DateOnly? CycleStart);

/// <summary>Returns bounded rejection or admitted server authority.</summary>
public sealed record PersonalProviderAdmission(PersonalProviderAdmissionCategory Category,
    PersonalProviderAuthoritySnapshot? Authority, PersonalProviderUsageStatus? Usage)
{
    public bool Succeeded => Category == PersonalProviderAdmissionCategory.Admitted;
    public static PersonalProviderAdmission Rejected(PersonalProviderAdmissionCategory category, PersonalProviderUsageStatus? usage = null) => new(category, null, usage);
}
