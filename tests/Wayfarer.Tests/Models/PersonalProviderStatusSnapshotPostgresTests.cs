using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Proves provider presentation remains wholly inside one PostgreSQL snapshot.</summary>
[Collection(PostgresEnvironmentEvidenceTestCollection.Name)]
public sealed class PersonalProviderStatusSnapshotPostgresTests(PostgresImportTestFixture fixture)
{
    public static TheoryData<ProviderDrift> Cases => new()
    {
        ProviderDrift.GeoapifyToMapbox,
        ProviderDrift.MapboxToGeoapify,
        ProviderDrift.CredentialReplacement,
        ProviderDrift.CapabilityGeneration,
        ProviderDrift.SelectionGeneration,
        ProviderDrift.VerificationInvalidation,
        ProviderDrift.ProfileRevocation,
        ProviderDrift.MapboxConsentInvalidation,
        ProviderDrift.GeoapifyGuardDisabled,
        ProviderDrift.GeoapifyLimitBelowUsage,
        ProviderDrift.MapboxMeterChanged
    };

    [PostgresTheory]
    [MemberData(nameof(Cases))]
    public async Task ConcurrentAuthorityDrift_ReturnsOneOldSnapshotThenFreshNewSnapshot(ProviderDrift drift)
    {
        fixture.RequireAvailable();
        var protection = new EphemeralDataProtectionProvider();
        var user = await fixture.CreateUserAsync();
        await SeedAsync(user.Id, InitialProvider(drift), protection);

        PersonalProviderInspection expectedOld;
        await using (var baseline = fixture.CreateContext())
            expectedOld = await Reader(baseline, protection).InspectPersistentGeocodingAsync(user.Id);
        var gate = new SelectionReadGate();
        (int Geoapify, int Mapbox) countsAfterMutation;
        PersonalProviderInspection oldSnapshot;
        await using (var presentation = fixture.CreateContext(gate))
        {
            var running = Reader(presentation, protection).InspectPersistentGeocodingAsync(user.Id);
            await gate.SelectionRead.WaitAsync(TimeSpan.FromSeconds(10));
            await MutateAsync(user.Id, drift, protection);
            countsAfterMutation = await AdmissionCountsAsync(user.Id);
            gate.Release();
            oldSnapshot = await running;
        }

        PersonalProviderInspection freshSnapshot;
        await using (var fresh = fixture.CreateContext())
            freshSnapshot = await Reader(fresh, protection).InspectPersistentGeocodingAsync(user.Id);

        AssertSameAuthorityState(expectedOld, oldSnapshot);
        Assert.NotEqual(AuthorityState(oldSnapshot), AuthorityState(freshSnapshot));
        AssertSnapshotComplete(oldSnapshot);
        AssertSnapshotComplete(freshSnapshot);
        Assert.Equal(countsAfterMutation, await AdmissionCountsAsync(user.Id));
        Assert.DoesNotContain("snapshot-secret", oldSnapshot.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("replacement-secret", freshSnapshot.ToString(), StringComparison.Ordinal);
    }

    private async Task SeedAsync(string userId, PersonalLocationProvider selected,
        IDataProtectionProvider protection)
    {
        await using var db = fixture.CreateContext();
        var owner = new PersonalProviderCredentialService(protection);
        var geoapify = Profile(userId, PersonalLocationProvider.Geoapify, owner);
        var mapbox = Profile(userId, PersonalLocationProvider.Mapbox, owner);
        mapbox.GrantPermanentGeocodingConsent(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        Verify(mapbox);
        var selection = PersonalLocationProviderSelection.Create(userId);
        selection.Select(PersonalProviderCapability.Geocoding, selected);
        db.AddRange(geoapify, mapbox, selection,
            new GeoapifyUsageGuard { UserId = userId, Enabled = true, CreditLimit = 5 },
            new GeoapifyUsageAdmission
            {
                UserId = userId, Credits = 3, Product = PersonalProviderProduct.Geocoding,
                AdmittedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
            },
            new MapboxProductMeter
            {
                UserId = userId, Product = PersonalProviderProduct.PermanentGeocoding,
                Enabled = true, Limit = 7, CycleStart = new(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1),
                AdmittedCount = 2
            });
        await db.SaveChangesAsync();
    }

    private static PersonalLocationProviderProfile Profile(string userId,
        PersonalLocationProvider provider, PersonalProviderCredentialService owner)
    {
        var profile = PersonalLocationProviderProfile.Create(userId, provider);
        owner.Replace(profile, "snapshot-secret");
        profile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
        Verify(profile);
        return profile;
    }

    private static void Verify(PersonalLocationProviderProfile profile)
    {
        profile.GeocodingVerification = PersonalProviderVerification.Verified;
        profile.GeocodingVerifiedCredentialGeneration = profile.CredentialGeneration;
        profile.GeocodingVerifiedConfigurationGeneration = profile.GeocodingGeneration;
    }

    private async Task MutateAsync(string userId, ProviderDrift drift, IDataProtectionProvider protection)
    {
        await using var db = fixture.CreateContext();
        var selection = await db.PersonalLocationProviderSelections.SingleAsync(x => x.UserId == userId);
        var provider = InitialProvider(drift);
        var profile = await db.PersonalLocationProviderProfiles.SingleAsync(x =>
            x.UserId == userId && x.ProviderKey == PersonalProviderKeys.Key(provider));
        switch (drift)
        {
            case ProviderDrift.GeoapifyToMapbox:
                selection.Select(PersonalProviderCapability.Geocoding, PersonalLocationProvider.Mapbox);
                break;
            case ProviderDrift.MapboxToGeoapify:
                selection.Select(PersonalProviderCapability.Geocoding, PersonalLocationProvider.Geoapify);
                break;
            case ProviderDrift.CredentialReplacement:
                new PersonalProviderCredentialService(protection).Replace(profile, "replacement-secret");
                break;
            case ProviderDrift.CapabilityGeneration:
                profile.GeocodingGeneration++;
                break;
            case ProviderDrift.SelectionGeneration:
                selection.GeocodingSelectionGeneration++;
                break;
            case ProviderDrift.VerificationInvalidation:
                profile.ClearVerification(PersonalProviderCapability.Geocoding);
                break;
            case ProviderDrift.ProfileRevocation:
                profile.RevokedAt = DateTimeOffset.UtcNow;
                break;
            case ProviderDrift.MapboxConsentInvalidation:
                profile.ClearPermanentGeocodingConsent();
                break;
            case ProviderDrift.GeoapifyGuardDisabled:
                (await db.GeoapifyUsageGuards.SingleAsync(x => x.UserId == userId)).Enabled = false;
                break;
            case ProviderDrift.GeoapifyLimitBelowUsage:
                (await db.GeoapifyUsageGuards.SingleAsync(x => x.UserId == userId)).CreditLimit = 2;
                break;
            case ProviderDrift.MapboxMeterChanged:
                var meter = await db.MapboxProductMeters.SingleAsync(x => x.UserId == userId
                    && x.Product == PersonalProviderProduct.PermanentGeocoding);
                meter.Limit = 1;
                meter.AdmittedCount = 4;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(drift));
        }
        await db.SaveChangesAsync();
    }

    private async Task<(int Geoapify, int Mapbox)> AdmissionCountsAsync(string userId)
    {
        await using var db = fixture.CreateContext();
        return (await db.GeoapifyUsageAdmissions.CountAsync(x => x.UserId == userId),
            (await db.MapboxProductMeters.SingleAsync(x => x.UserId == userId
                && x.Product == PersonalProviderProduct.PermanentGeocoding)).AdmittedCount);
    }

    private static void AssertSnapshotComplete(PersonalProviderInspection value)
    {
        Assert.Contains(value.ProviderKey, new[] { "geoapify", "mapbox" });
        Assert.NotNull(value.Usage);
        Assert.NotEmpty(value.Usage!.Unit);
        Assert.True(value.Usage.Limit > 0);
        Assert.True(value.Usage.Used >= 0);
        if (value.Category == PersonalProviderAdmissionCategory.Admitted)
        {
            Assert.NotNull(value.Binding);
            Assert.Equal(value.ProviderKey, value.Binding!.ProviderKey);
            Assert.Equal(PersonalProviderVerification.Verified, value.Binding.Verification);
            Assert.Equal(value.Binding.CredentialGeneration, value.Binding.VerifiedCredentialGeneration);
            Assert.Equal(value.Binding.CapabilityGeneration, value.Binding.VerifiedCapabilityGeneration);
            if (value.ProviderKey == "mapbox")
            {
                Assert.NotNull(value.Binding.ConsentVersion);
                Assert.NotNull(value.Binding.ConsentedAt);
                Assert.Equal(value.Binding.CredentialGeneration, value.Binding.ConsentCredentialGeneration);
            }
        }
        Assert.Equal(value.GuardEnabled && value.Usage.Used >= value.Usage.Limit, value.Exhausted);
        Assert.Equal(value.Category == PersonalProviderAdmissionCategory.Admitted && !value.Exhausted,
            value.Available);
        Assert.Equal(value.Exhausted, value.NextAvailableAt.HasValue);
    }

    private static void AssertSameAuthorityState(
        PersonalProviderInspection expected, PersonalProviderInspection actual)
    {
        Assert.Equal(AuthorityState(expected), AuthorityState(actual));
        Assert.Equal(expected.Usage?.CycleStart, actual.Usage?.CycleStart);
        Assert.Equal(expected.Usage?.RollingCutoff.HasValue, actual.Usage?.RollingCutoff.HasValue);
        Assert.Equal(expected.NextAvailableAt.HasValue, actual.NextAvailableAt.HasValue);
    }

    private static object AuthorityState(PersonalProviderInspection value) => new
    {
        value.Category,
        value.ProviderKey,
        value.GuardEnabled,
        value.Exhausted,
        value.Binding,
        Used = value.Usage?.Used,
        Limit = value.Usage?.Limit,
        Unit = value.Usage?.Unit
    };

    private static PersonalLocationProvider InitialProvider(ProviderDrift drift) => drift is
        ProviderDrift.MapboxToGeoapify or ProviderDrift.MapboxConsentInvalidation
        or ProviderDrift.MapboxMeterChanged ? PersonalLocationProvider.Mapbox : PersonalLocationProvider.Geoapify;

    private static PersonalProviderStatusReader Reader(
        Wayfarer.Models.ApplicationDbContext db, IDataProtectionProvider protection) => new(db,
        new PersonalProviderCredentialService(protection),
        new ConfigurationBuilder().AddInMemoryCollection().Build());

    private sealed class SelectionReadGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource selectionRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task SelectionRead => selectionRead.Task;
        internal void Release() => release.TrySetResult();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("PersonalLocationProviderSelections", StringComparison.Ordinal)
                && !selectionRead.Task.IsCompleted)
            {
                selectionRead.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    public enum ProviderDrift
    {
        GeoapifyToMapbox,
        MapboxToGeoapify,
        CredentialReplacement,
        CapabilityGeneration,
        SelectionGeneration,
        VerificationInvalidation,
        ProfileRevocation,
        MapboxConsentInvalidation,
        GeoapifyGuardDisabled,
        GeoapifyLimitBelowUsage,
        MapboxMeterChanged
    }
}
