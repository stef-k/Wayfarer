using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;

namespace Wayfarer.Services.LocationProviders;

/// <summary>Owns the lowest shared credential-authority and durable usage-admission seam before provider HTTP.</summary>
public interface IPersonalProviderInspection
{
    Task<PersonalProviderInspection> InspectPersistentGeocodingAsync(
        string userId, CancellationToken cancellationToken = default);
}

/// <summary>Owns current provider contact authority and durable admissions.</summary>
public sealed class PersonalProviderContactGate(
    ApplicationDbContext dbContext, PersonalProviderCredentialService credentials,
    LegacyMapboxMigrationService legacyMigration, IConfiguration configuration) : IPersonalProviderInspection
{
    /// <summary>Reads current persistent-geocoding authority and usage without admitting provider contact.</summary>
    public async Task<PersonalProviderInspection> InspectPersistentGeocodingAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        var authority = await ResolveAsync(userId, PersonalProviderCapability.Geocoding, cancellationToken);
        var selection = await dbContext.PersonalLocationProviderSelections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        var providerKey = selection?.GeocodingProviderKey;
        var now = DateTimeOffset.UtcNow;
        PersonalProviderUsageStatus? usage = null;
        var guardEnabled = false;
        DateTimeOffset? nextAvailable = null;

        if (providerKey == "geoapify")
        {
            var guard = await dbContext.GeoapifyUsageGuards.AsNoTracking()
                .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
            var limit = guard?.CreditLimit
                ?? configuration.GetValue("LocationProviders:Geoapify:RollingCreditLimit", 2500);
            guardEnabled = guard?.Enabled ?? true;
            var cutoff = now.AddHours(-24);
            var admissions = await dbContext.GeoapifyUsageAdmissions.AsNoTracking()
                .Where(item => item.UserId == userId && item.AdmittedAt > cutoff)
                .OrderBy(item => item.AdmittedAt).ToListAsync(cancellationToken);
            var used = admissions.Sum(item => item.Credits);
            if (guardEnabled && used >= limit && admissions.Count > 0)
                nextAvailable = admissions[0].AdmittedAt.AddHours(24);
            usage = new(used, limit, "credits", cutoff, null);
        }
        else if (providerKey == "mapbox")
        {
            var meter = await dbContext.MapboxProductMeters.AsNoTracking().SingleOrDefaultAsync(item =>
                item.UserId == userId && item.Product == PersonalProviderProduct.PermanentGeocoding,
                cancellationToken);
            var limit = meter?.Limit
                ?? configuration.GetValue("LocationProviders:Mapbox:PermanentGeocodingLimit", 1000);
            guardEnabled = meter?.Enabled ?? true;
            var cycle = new DateOnly(now.Year, now.Month, 1);
            var used = meter?.CycleStart == cycle ? meter.AdmittedCount : 0;
            if (guardEnabled && used >= limit)
                nextAvailable = new DateTimeOffset(cycle.AddMonths(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            usage = new(used, limit, "contacts", null, cycle);
        }

        var exhausted = usage != null && guardEnabled && usage.Used >= usage.Limit;
        return new(authority.Category, providerKey, guardEnabled, exhausted, nextAvailable, usage,
            authority.Succeeded ? new(authority.ProviderKey!, authority.ProfileId,
                authority.CredentialGeneration, authority.CapabilityGeneration, authority.SelectionGeneration,
                authority.Verification, authority.VerifiedCredentialGeneration, authority.VerifiedCapabilityGeneration,
                authority.ConsentVersion, authority.ConsentedAt, authority.ConsentCredentialGeneration) : null);
    }

    /// <summary>Resolves the selected geocoding provider and admits its exact persistent product cost.</summary>
    public async Task<PersonalProviderAdmission> AdmitPersistentGeocodingAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        var selection = await dbContext.Set<PersonalLocationProviderSelection>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        var product = selection?.GeocodingProviderKey switch
        {
            "geoapify" => PersonalProviderProduct.Geocoding,
            "mapbox" => PersonalProviderProduct.PermanentGeocoding,
            _ => (PersonalProviderProduct?)null
        };
        return product.HasValue
            ? await AdmitAsync(userId, PersonalProviderCapability.Geocoding, product.Value, 1, cancellationToken)
            : PersonalProviderAdmission.Rejected(PersonalProviderAdmissionCategory.NoProviderSelected);
    }

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
            authority.SelectionGeneration, authority.ConsentVersion, authority.ConsentedAt,
            authority.ConsentCredentialGeneration, authority.ProfileId, authority.Verification,
            authority.VerifiedCredentialGeneration, authority.VerifiedCapabilityGeneration);
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
                profile.PermanentGeocodingConsentedAt, profile.PermanentGeocodingConsentCredentialGeneration), admitted.Usage);
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
            && profile.PermanentGeocodingConsentedAt == snapshot.ConsentedAt
            && profile.PermanentGeocodingConsentCredentialGeneration == snapshot.ConsentCredentialGeneration;
    }

    /// <summary>Atomically records verification only for the authority that made the admitted contact.</summary>
    public async Task<bool> TryRecordMapboxPermanentVerificationAsync(
        PersonalProviderAuthoritySnapshot snapshot, PersonalProviderVerification verification,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.Set<PersonalLocationProviderProfile>().Where(profile =>
            profile.UserId == snapshot.UserId && profile.ProviderKey == snapshot.ProviderKey
            && profile.RevokedAt == null && profile.GeocodingAuthorized
            && profile.CredentialGeneration == snapshot.CredentialGeneration
            && profile.GeocodingGeneration == snapshot.CapabilityGeneration
            && profile.PermanentGeocodingConsentVersion == snapshot.ConsentVersion
            && profile.PermanentGeocodingConsentedAt == snapshot.ConsentedAt
            && profile.PermanentGeocodingConsentCredentialGeneration == snapshot.ConsentCredentialGeneration);
        var verified = verification == PersonalProviderVerification.Verified;
        var now = DateTimeOffset.UtcNow;
        if (dbContext.Database.IsRelational())
        {
            var count = await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(profile => profile.GeocodingVerification, verification)
                .SetProperty(profile => profile.GeocodingVerifiedCredentialGeneration, verified ? snapshot.CredentialGeneration : null)
                .SetProperty(profile => profile.GeocodingVerifiedConfigurationGeneration, verified ? snapshot.CapabilityGeneration : null)
                .SetProperty(profile => profile.UpdatedAt, now), cancellationToken);
            dbContext.ChangeTracker.Clear();
            return count == 1;
        }
        var match = await query.SingleOrDefaultAsync(cancellationToken);
        if (match == null) return false;
        match.GeocodingVerification = verification;
        match.GeocodingVerifiedCredentialGeneration = verified ? snapshot.CredentialGeneration : null;
        match.GeocodingVerifiedConfigurationGeneration = verified ? snapshot.CapabilityGeneration : null;
        match.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Admits one explicit Geoapify capability verification without requiring selection or prior verification.</summary>
    public async Task<PersonalProviderAdmission> AdmitGeoapifyVerificationAsync(
        string userId, PersonalProviderCapability capability, CancellationToken cancellationToken = default)
    {
        var profile = await dbContext.Set<PersonalLocationProviderProfile>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.ProviderKey == "geoapify", cancellationToken);
        if (profile == null || profile.RevokedAt != null || !profile.IsAuthorized(capability))
            return PersonalProviderAdmission.Rejected(PersonalProviderAdmissionCategory.Unauthorized);
        var read = credentials.Read(profile);
        if (!read.Succeeded)
            return PersonalProviderAdmission.Rejected(PersonalProviderAdmissionCategory.CredentialUnavailable);
        var product = capability == PersonalProviderCapability.Geocoding
            ? PersonalProviderProduct.Geocoding : PersonalProviderProduct.Routing;
        var admitted = await AdmitGeoapifyAsync(userId, product, 1, cancellationToken);
        if (!admitted.Succeeded) return admitted;
        var generation = capability == PersonalProviderCapability.Geocoding
            ? profile.GeocodingGeneration : profile.RoutingGeneration;
        return new(PersonalProviderAdmissionCategory.Admitted,
            new(userId, "geoapify", capability, read.Credential!, profile.CredentialGeneration, generation, 0),
            admitted.Usage);
    }

    /// <summary>Revalidates Geoapify verification authority without requiring selection or verified state.</summary>
    public async Task<bool> IsGeoapifyVerificationCurrentAsync(
        PersonalProviderAuthoritySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var profile = await dbContext.Set<PersonalLocationProviderProfile>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == snapshot.UserId && item.ProviderKey == "geoapify", cancellationToken);
        return profile != null && profile.RevokedAt == null && profile.IsAuthorized(snapshot.Capability)
            && profile.CredentialGeneration == snapshot.CredentialGeneration
            && (snapshot.Capability == PersonalProviderCapability.Geocoding
                ? profile.GeocodingGeneration : profile.RoutingGeneration) == snapshot.CapabilityGeneration;
    }

    /// <summary>Atomically records one Geoapify capability result only for the authority that contacted the provider.</summary>
    public async Task<bool> TryRecordGeoapifyVerificationAsync(
        PersonalProviderAuthoritySnapshot snapshot, PersonalProviderVerification verification,
        CancellationToken cancellationToken = default)
    {
        if (snapshot.ProviderKey != "geoapify") return false;
        var query = dbContext.Set<PersonalLocationProviderProfile>().Where(profile =>
            profile.UserId == snapshot.UserId && profile.ProviderKey == "geoapify" && profile.RevokedAt == null
            && profile.CredentialGeneration == snapshot.CredentialGeneration
            && (snapshot.Capability == PersonalProviderCapability.Geocoding
                ? profile.GeocodingAuthorized && profile.GeocodingGeneration == snapshot.CapabilityGeneration
                : profile.RoutingAuthorized && profile.RoutingGeneration == snapshot.CapabilityGeneration));
        if (dbContext.Database.IsRelational())
        {
            var verified = verification == PersonalProviderVerification.Verified;
            var now = DateTimeOffset.UtcNow;
            var count = snapshot.Capability == PersonalProviderCapability.Geocoding
                ? await query.ExecuteUpdateAsync(setters => setters
                    .SetProperty(profile => profile.GeocodingVerification, verification)
                    .SetProperty(profile => profile.GeocodingVerifiedCredentialGeneration,
                        verified ? snapshot.CredentialGeneration : null)
                    .SetProperty(profile => profile.GeocodingVerifiedConfigurationGeneration,
                        verified ? snapshot.CapabilityGeneration : null)
                    .SetProperty(profile => profile.UpdatedAt, now), cancellationToken)
                : await query.ExecuteUpdateAsync(setters => setters
                    .SetProperty(profile => profile.RoutingVerification, verification)
                    .SetProperty(profile => profile.RoutingVerifiedCredentialGeneration,
                        verified ? snapshot.CredentialGeneration : null)
                    .SetProperty(profile => profile.RoutingVerifiedConfigurationGeneration,
                        verified ? snapshot.CapabilityGeneration : null)
                    .SetProperty(profile => profile.UpdatedAt, now), cancellationToken);
            dbContext.ChangeTracker.Clear();
            return count == 1;
        }
        var profile = await query.SingleOrDefaultAsync(cancellationToken);
        if (profile == null) return false;
        credentials.RecordVerification(profile, snapshot.Capability, verification);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Revalidates bounded authority immediately before contact and result persistence.</summary>
    public async Task<bool> IsCurrentAsync(
        PersonalProviderAuthoritySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var current = await ResolveAsync(snapshot.UserId, snapshot.Capability, cancellationToken);
        return snapshot.Capability is PersonalProviderCapability.Geocoding or PersonalProviderCapability.Routing
            && current.Succeeded && current.ProviderKey == snapshot.ProviderKey
            && current.ProfileId == snapshot.ProfileId
            && current.CredentialGeneration == snapshot.CredentialGeneration
            && current.CapabilityGeneration == snapshot.CapabilityGeneration
            && current.SelectionGeneration == snapshot.SelectionGeneration
            && current.Verification == snapshot.Verification
            && current.VerifiedCredentialGeneration == snapshot.VerifiedCredentialGeneration
            && current.VerifiedCapabilityGeneration == snapshot.VerifiedCapabilityGeneration
            && current.ConsentVersion == snapshot.ConsentVersion
            && current.ConsentedAt == snapshot.ConsentedAt
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

        if (providerKey == "mapbox" && capability == PersonalProviderCapability.Geocoding
            && !profile.HasCurrentPermanentGeocodingConsent())
            return ResolvedAuthority.Fail(PersonalProviderAdmissionCategory.ConsentRequired);
        var verified = capability == PersonalProviderCapability.Geocoding
            ? profile.GeocodingVerification == PersonalProviderVerification.Verified
              && profile.GeocodingVerifiedCredentialGeneration == profile.CredentialGeneration
              && profile.GeocodingVerifiedConfigurationGeneration == profile.GeocodingGeneration
            : profile.RoutingVerification == PersonalProviderVerification.Verified
              && profile.RoutingVerifiedCredentialGeneration == profile.CredentialGeneration
              && profile.RoutingVerifiedConfigurationGeneration == profile.RoutingGeneration;
        if (!verified) return ResolvedAuthority.Fail(PersonalProviderAdmissionCategory.Unverified);
        return new(true, PersonalProviderAdmissionCategory.Admitted, providerKey, read.Credential,
            profile.CredentialGeneration,
            capability == PersonalProviderCapability.Geocoding ? profile.GeocodingGeneration : profile.RoutingGeneration,
            capability == PersonalProviderCapability.Geocoding
                ? selection!.GeocodingSelectionGeneration : selection!.RoutingSelectionGeneration,
            profile.PermanentGeocodingConsentVersion, profile.PermanentGeocodingConsentedAt,
            profile.PermanentGeocodingConsentCredentialGeneration, profile.Id,
            capability == PersonalProviderCapability.Geocoding ? profile.GeocodingVerification : profile.RoutingVerification,
            capability == PersonalProviderCapability.Geocoding
                ? profile.GeocodingVerifiedCredentialGeneration : profile.RoutingVerifiedCredentialGeneration,
            capability == PersonalProviderCapability.Geocoding
                ? profile.GeocodingVerifiedConfigurationGeneration : profile.RoutingVerifiedConfigurationGeneration);
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
        var expired = dbContext.Set<GeoapifyUsageAdmission>()
            .Where(item => item.UserId == userId && item.AdmittedAt <= cutoff);
        if (dbContext.Database.IsRelational())
            await expired.ExecuteDeleteAsync(cancellationToken);
        else
            dbContext.RemoveRange(await expired.ToListAsync(cancellationToken));
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
        int? ConsentVersion = null, DateTimeOffset? ConsentedAt = null, int? ConsentCredentialGeneration = null,
        Guid? ProfileId = null, PersonalProviderVerification Verification = PersonalProviderVerification.Unverified,
        int? VerifiedCredentialGeneration = null, int? VerifiedCapabilityGeneration = null)
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
        int? consentVersion = null, DateTimeOffset? consentedAt = null, int? consentCredentialGeneration = null,
        Guid? profileId = null, PersonalProviderVerification verification = PersonalProviderVerification.Unverified,
        int? verifiedCredentialGeneration = null, int? verifiedCapabilityGeneration = null)
    {
        UserId = userId; ProviderKey = providerKey; Capability = capability; Credential = credential;
        CredentialGeneration = credentialGeneration; CapabilityGeneration = capabilityGeneration;
        SelectionGeneration = selectionGeneration;
        ConsentVersion = consentVersion; ConsentedAt = consentedAt; ConsentCredentialGeneration = consentCredentialGeneration;
        ProfileId = profileId; Verification = verification; VerifiedCredentialGeneration = verifiedCredentialGeneration;
        VerifiedCapabilityGeneration = verifiedCapabilityGeneration;
    }
    public string UserId { get; }
    public string ProviderKey { get; }
    public PersonalProviderCapability Capability { get; }
    [JsonIgnore] public string Credential { get; }
    public int CredentialGeneration { get; }
    public int CapabilityGeneration { get; }
    public int SelectionGeneration { get; }
    public int? ConsentVersion { get; }
    public DateTimeOffset? ConsentedAt { get; }
    public int? ConsentCredentialGeneration { get; }
    public Guid? ProfileId { get; }
    public PersonalProviderVerification Verification { get; }
    public int? VerifiedCredentialGeneration { get; }
    public int? VerifiedCapabilityGeneration { get; }
    public override string ToString() => $"PersonalProviderAuthoritySnapshot {{ ProviderKey = {ProviderKey}, Capability = {Capability}, CredentialGeneration = {CredentialGeneration}, CapabilityGeneration = {CapabilityGeneration}, SelectionGeneration = {SelectionGeneration} }}";
}

/// <summary>Contains only bounded usage status.</summary>
public sealed record PersonalProviderUsageStatus(int Used, int Limit, string Unit, DateTimeOffset? RollingCutoff, DateOnly? CycleStart);

/// <summary>Contains bounded current authority and usage facts without credential material.</summary>
public sealed record PersonalProviderInspection(PersonalProviderAdmissionCategory Category, string? ProviderKey,
    bool GuardEnabled, bool Exhausted, DateTimeOffset? NextAvailableAt, PersonalProviderUsageStatus? Usage,
    PersonalProviderAuthorityBinding? Binding)
{
    public bool Available => Category == PersonalProviderAdmissionCategory.Admitted && !Exhausted;
}

/// <summary>Identifies current durable authority for relational attempt classification.</summary>
public sealed record PersonalProviderAuthorityBinding(string ProviderKey, Guid? ProfileId,
    int CredentialGeneration, int CapabilityGeneration, int SelectionGeneration,
    PersonalProviderVerification Verification, int? VerifiedCredentialGeneration,
    int? VerifiedCapabilityGeneration, int? ConsentVersion, DateTimeOffset? ConsentedAt,
    int? ConsentCredentialGeneration);

/// <summary>Returns bounded rejection or admitted server authority.</summary>
public sealed record PersonalProviderAdmission(PersonalProviderAdmissionCategory Category,
    PersonalProviderAuthoritySnapshot? Authority, PersonalProviderUsageStatus? Usage)
{
    public bool Succeeded => Category == PersonalProviderAdmissionCategory.Admitted;
    public static PersonalProviderAdmission Rejected(PersonalProviderAdmissionCategory category, PersonalProviderUsageStatus? usage = null) => new(category, null, usage);
}
