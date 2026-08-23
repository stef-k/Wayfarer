using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Defines the shared personal-provider authority required by issues 500 through 502.</summary>
public sealed class PersonalLocationProviderFoundationTests : TestBase
{
    [Fact]
    public void CredentialOwner_ProtectsProviderProfileCredential()
    {
        var profile = PersonalLocationProviderProfile.Create("user-1", PersonalLocationProvider.Mapbox);
        var owner = new PersonalProviderCredentialService(new EphemeralDataProtectionProvider());

        owner.Replace(profile, "secret-mapbox-key");

        Assert.DoesNotContain("secret-mapbox-key", profile.ProtectedCredential, StringComparison.Ordinal);
        Assert.Equal("secret-mapbox-key", owner.Read(profile).Credential);
    }

    [Fact]
    public void Profile_AuthorizesGeocodingAndRoutingIndependently()
    {
        var profile = PersonalLocationProviderProfile.Create("user-1", PersonalLocationProvider.Geoapify);

        profile.SetAuthorization(PersonalProviderCapability.Geocoding, true);

        Assert.True(profile.IsAuthorized(PersonalProviderCapability.Geocoding));
        Assert.False(profile.IsAuthorized(PersonalProviderCapability.Routing));
    }

    [Fact]
    public void SwitchingProvider_RetainsInactiveProfileCredential()
    {
        var mapbox = PersonalLocationProviderProfile.Create("user-1", PersonalLocationProvider.Mapbox);
        var owner = new PersonalProviderCredentialService(new EphemeralDataProtectionProvider());
        owner.Replace(mapbox, "retained-key");
        var selection = PersonalLocationProviderSelection.Create("user-1");

        selection.Select(PersonalProviderCapability.Geocoding, PersonalLocationProvider.Mapbox);
        selection.Select(PersonalProviderCapability.Geocoding, PersonalLocationProvider.Geoapify);

        Assert.Equal("retained-key", owner.Read(mapbox).Credential);
    }

    [Fact]
    public void LegacyMigration_DoesNotRetireMapboxUntilProtectedReadbackSucceeds()
    {
        var decision = LegacyMapboxMigration.Decide(
            protectedRead: PersonalCredentialRead.Unavailable,
            recognizedLegacyValues: ["legacy-key"]);

        Assert.False(decision.RetireLegacy);
        Assert.Equal(LegacyMapboxMigrationState.ProtectedCredentialUnavailable, decision.State);
    }

    [Fact]
    public void GeoapifyAdmission_UsesOneRollingSharedCreditPool()
    {
        var ledger = new PersonalProviderUsageLedger();
        var now = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

        Assert.True(ledger.TryAdmitGeoapify(now, 2_500, 2_500, PersonalProviderProduct.Geocoding));
        Assert.False(ledger.TryAdmitGeoapify(now, 2_500, 1, PersonalProviderProduct.Routing));
    }

    [Fact]
    public void MapboxAdmission_UsesIndependentProductCounters()
    {
        var ledger = new PersonalProviderUsageLedger();
        var cycle = new DateOnly(2026, 8, 1);

        Assert.True(ledger.TryAdmitMapbox(cycle, PersonalProviderProduct.PermanentGeocoding, 1, 1));
        Assert.False(ledger.TryAdmitMapbox(cycle, PersonalProviderProduct.PermanentGeocoding, 1, 1));
        Assert.True(ledger.TryAdmitMapbox(cycle, PersonalProviderProduct.Directions, 1, 1));
    }

    [Fact]
    public async Task LegacyMigration_ProtectsBeforeRetiringAndPreservesUnrelatedTokens()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "legacy-user", username: "legacy");
        db.Users.Add(user);
        db.ApiTokens.AddRange(
            new ApiToken { Id = 8001, Name = " MapBOX ", Token = "legacy-key", UserId = user.Id, User = user },
            new ApiToken { Id = 8002, Name = "mobile", TokenHash = "hash", UserId = user.Id, User = user });
        await db.SaveChangesAsync();
        var owner = new PersonalProviderCredentialService(new EphemeralDataProtectionProvider());

        var result = await new LegacyMapboxMigrationService(db, owner).MigrateAsync(user.Id);

        var profile = await db.PersonalLocationProviderProfiles.SingleAsync();
        Assert.True(result.ProtectedCredentialReady);
        Assert.Equal("legacy-key", owner.Read(profile).Credential);
        Assert.True(profile.GeocodingAuthorized);
        Assert.False(profile.RoutingAuthorized);
        Assert.DoesNotContain(await db.ApiTokens.ToListAsync(), item => PersonalProviderKeys.IsLegacyMapbox(item.Name));
        Assert.Contains(await db.ApiTokens.ToListAsync(), item => item.Name == "mobile");
    }

    [Fact]
    public async Task LegacyMigration_PreservesDistinctRecognizedValuesAndFailsClosed()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "conflict-user", username: "conflict");
        db.Users.Add(user);
        db.ApiTokens.AddRange(
            new ApiToken { Id = 8101, Name = "Mapbox", Token = "first", UserId = user.Id, User = user },
            new ApiToken { Id = 8102, Name = "mapBOX", Token = "second", UserId = user.Id, User = user });
        await db.SaveChangesAsync();

        var result = await new LegacyMapboxMigrationService(db,
            new PersonalProviderCredentialService(new EphemeralDataProtectionProvider())).MigrateAsync(user.Id);

        Assert.Equal(LegacyMapboxMigrationState.Conflict, result.State);
        Assert.Equal(2, await db.ApiTokens.CountAsync());
        Assert.Null((await db.PersonalLocationProviderProfiles.SingleAsync()).ProtectedCredential);
    }
}
