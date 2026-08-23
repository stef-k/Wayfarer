using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
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
            new ApiToken { Id = 8002, Name = "mobile", TokenHash = "hash", UserId = user.Id, User = user },
            new ApiToken { Id = 8003, Name = "MyMapboxBackup", Token = "unrelated", UserId = user.Id, User = user });
        await db.SaveChangesAsync();
        var owner = new PersonalProviderCredentialService(new EphemeralDataProtectionProvider());

        var result = await new LegacyMapboxMigrationService(db, owner).MigrateAsync(user.Id);

        var profile = await db.PersonalLocationProviderProfiles.SingleAsync();
        Assert.True(result.ProtectedCredentialReady);
        Assert.Equal("legacy-key", owner.Read(profile).Credential);
        Assert.True(profile.GeocodingAuthorized);
        Assert.False(profile.RoutingAuthorized);
        Assert.DoesNotContain(await db.ApiTokens.IgnoreQueryFilters().ToListAsync(), item => PersonalProviderKeys.IsLegacyMapbox(item.Name));
        Assert.Contains(await db.ApiTokens.ToListAsync(), item => item.Name == "mobile");
        Assert.Contains(await db.ApiTokens.ToListAsync(), item => item.Name == "MyMapboxBackup");
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
        Assert.Equal(2, await db.ApiTokens.IgnoreQueryFilters().CountAsync());
        Assert.Null((await db.PersonalLocationProviderProfiles.SingleAsync()).ProtectedCredential);
    }

    [Fact]
    public void PersistentKeyRing_ReadsCredentialAfterServiceRecreation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wayfarer-provider-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            var profile = PersonalLocationProviderProfile.Create("restart-user", PersonalLocationProvider.Geoapify);
            var first = new PersonalProviderCredentialService(DataProtectionProvider.Create(
                new DirectoryInfo(path)));
            first.Replace(profile, "restart-safe-key");

            var recreated = new PersonalProviderCredentialService(DataProtectionProvider.Create(
                new DirectoryInfo(path)));

            Assert.Equal("restart-safe-key", recreated.Read(profile).Credential);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void ContactAuthority_RedactsCredentialFromSerializationAndDiagnostics()
    {
        var snapshot = new PersonalProviderAuthoritySnapshot("user", "mapbox",
            PersonalProviderCapability.Geocoding, "never-disclose", 2, 3, 4);

        Assert.DoesNotContain("never-disclose", snapshot.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("never-disclose", JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
        Assert.DoesNotContain("never-disclose", new PersonalCredentialRead(true, "never-disclose").ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacementAndRevocation_AdvanceGenerationAndInvalidateBothCapabilities()
    {
        var profile = PersonalLocationProviderProfile.Create("generation-user", PersonalLocationProvider.Mapbox);
        var owner = new PersonalProviderCredentialService(new EphemeralDataProtectionProvider());
        owner.Replace(profile, "first");
        profile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
        profile.SetAuthorization(PersonalProviderCapability.Routing, true);
        owner.RecordVerification(profile, PersonalProviderCapability.Geocoding, PersonalProviderVerification.Verified);
        owner.RecordVerification(profile, PersonalProviderCapability.Routing, PersonalProviderVerification.Verified);
        var beforeReplacement = profile.CredentialGeneration;

        owner.Replace(profile, "second");

        Assert.True(profile.CredentialGeneration > beforeReplacement);
        Assert.Equal(PersonalProviderVerification.Unverified, profile.GeocodingVerification);
        Assert.Equal(PersonalProviderVerification.Unverified, profile.RoutingVerification);
        Assert.True(profile.GeocodingAuthorized);
        Assert.True(profile.RoutingAuthorized);
        var beforeRevocation = profile.CredentialGeneration;

        owner.Revoke(profile);

        Assert.True(profile.CredentialGeneration > beforeRevocation);
        Assert.False(profile.GeocodingAuthorized);
        Assert.False(profile.RoutingAuthorized);
        Assert.Null(profile.ProtectedCredential);
    }

    [Fact]
    public async Task LegacyMigration_SameValueAliasesAndRerunConverge()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "alias-user", username: "alias");
        db.Users.Add(user);
        db.ApiTokens.AddRange(
            new ApiToken { Id = 8201, Name = "Mapbox", Token = "same", UserId = user.Id, User = user },
            new ApiToken { Id = 8202, Name = " mapBOX ", Token = "same", UserId = user.Id, User = user });
        await db.SaveChangesAsync();
        var service = new LegacyMapboxMigrationService(db,
            new PersonalProviderCredentialService(new EphemeralDataProtectionProvider()));

        var first = await service.MigrateAsync(user.Id);
        var rerun = await service.MigrateAsync(user.Id);

        Assert.Equal(2, first.RetiredLegacyRows);
        Assert.True(first.ProtectedCredentialReady);
        Assert.Equal(LegacyMapboxMigrationState.Migrated, rerun.State);
        Assert.Empty(await db.ApiTokens.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task LegacyMigration_InvalidCiphertextAndRevocationPreservePlaintext()
    {
        var db = CreateDbContext();
        var invalidUser = TestDataFixtures.CreateUser(id: "invalid-user", username: "invalid");
        var revokedUser = TestDataFixtures.CreateUser(id: "revoked-user", username: "revoked");
        db.Users.AddRange(invalidUser, revokedUser);
        var invalid = PersonalLocationProviderProfile.Create(invalidUser.Id, PersonalLocationProvider.Mapbox);
        invalid.ProtectedCredential = "invalid-ciphertext";
        var revoked = PersonalLocationProviderProfile.Create(revokedUser.Id, PersonalLocationProvider.Mapbox);
        revoked.RevokedAt = DateTimeOffset.UtcNow;
        db.AddRange(invalid, revoked);
        db.ApiTokens.AddRange(
            new ApiToken { Id = 8301, Name = "Mapbox", Token = "legacy-invalid", UserId = invalidUser.Id, User = invalidUser },
            new ApiToken { Id = 8302, Name = "Mapbox", Token = "legacy-revoked", UserId = revokedUser.Id, User = revokedUser });
        await db.SaveChangesAsync();
        var service = new LegacyMapboxMigrationService(db,
            new PersonalProviderCredentialService(new EphemeralDataProtectionProvider()));

        Assert.Equal(LegacyMapboxMigrationState.ProtectedCredentialUnavailable,
            (await service.MigrateAsync(invalidUser.Id)).State);
        Assert.Equal(LegacyMapboxMigrationState.Revoked, (await service.MigrateAsync(revokedUser.Id)).State);
        Assert.Equal(2, await db.ApiTokens.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task LegacyMigration_MatchingProtectedValueWinsAndEnablesOnlyGeocoding()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "matching-user", username: "matching");
        db.Users.Add(user);
        var owner = new PersonalProviderCredentialService(new EphemeralDataProtectionProvider());
        var profile = PersonalLocationProviderProfile.Create(user.Id, PersonalLocationProvider.Mapbox);
        owner.Replace(profile, "matching-key");
        db.Add(profile);
        db.ApiTokens.Add(new ApiToken
        { Id = 8401, Name = "Mapbox", Token = "matching-key", UserId = user.Id, User = user });
        await db.SaveChangesAsync();

        var result = await new LegacyMapboxMigrationService(db, owner).MigrateAsync(user.Id);

        Assert.True(result.ProtectedCredentialReady);
        Assert.Equal("matching-key", owner.Read(profile).Credential);
        Assert.True(profile.GeocodingAuthorized);
        Assert.False(profile.RoutingAuthorized);
        Assert.Empty(await db.ApiTokens.IgnoreQueryFilters().ToListAsync());
    }
}
