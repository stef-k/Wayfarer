using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Proves provider-native usage authority on guarded PostgreSQL.</summary>
[Collection(PostgresEnvironmentEvidenceTestCollection.Name)]
public sealed class PersonalProviderUsagePostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresFact]
    public async Task LegacyMapboxMigration_BypassesProductionFilterAndPreservesUnrelatedData()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var retainedTag = new Tag { Id = Guid.NewGuid(), Name = "Retained domain data", Slug = $"retained-{Guid.NewGuid():N}" };
        await using (var setup = fixture.CreateContext())
        {
            var trackedUser = await setup.Users.SingleAsync(item => item.Id == user.Id);
            setup.Add(retainedTag);
            setup.ApiTokens.AddRange(
                new ApiToken { Name = " MapBOX ", Token = "legacy-mapbox-key", UserId = user.Id, User = trackedUser },
                new ApiToken { Name = "mobile", Token = "retained-token", UserId = user.Id, User = trackedUser },
                new ApiToken { Name = "MyMapboxBackup", Token = "retained-substring", UserId = user.Id, User = trackedUser });
            await setup.SaveChangesAsync();
            Assert.DoesNotContain(await setup.ApiTokens.ToListAsync(), token =>
                string.Equals(token.Name.Trim(), "Mapbox", StringComparison.OrdinalIgnoreCase));
        }
        fixture.RegisterTag(retainedTag);

        var protection = new EphemeralDataProtectionProvider();
        await using (var migrate = fixture.CreateContext())
        {
            var owner = new PersonalProviderCredentialService(protection);
            var result = await new LegacyMapboxMigrationService(migrate, owner).MigrateAsync(user.Id);
            var profile = await migrate.PersonalLocationProviderProfiles.SingleAsync(item => item.UserId == user.Id);

            Assert.True(result.ProtectedCredentialReady);
            Assert.Equal("legacy-mapbox-key", owner.Read(profile).Credential);
            Assert.True(profile.GeocodingAuthorized);
            Assert.False(profile.RoutingAuthorized);
        }

        await using var verify = fixture.CreateContext();
        var remaining = await verify.ApiTokens.IgnoreQueryFilters().Where(token => token.UserId == user.Id).ToListAsync();
        Assert.Equal(2, remaining.Count);
        Assert.DoesNotContain(remaining, token =>
            string.Equals(token.Name.Trim(), "Mapbox", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(remaining, token => token.Name == "mobile");
        Assert.Contains(remaining, token => token.Name == "MyMapboxBackup");
        Assert.True(await verify.Tags.AnyAsync(tag => tag.Id == retainedTag.Id));
    }

    [PostgresFact]
    public async Task LegacyMapboxMigration_DistinctAliasesPreserveConflictRows()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        await using (var setup = fixture.CreateContext())
        {
            var trackedUser = await setup.Users.SingleAsync(item => item.Id == user.Id);
            setup.ApiTokens.AddRange(
                new ApiToken { Name = "Mapbox", Token = "first-value", UserId = user.Id, User = trackedUser },
                new ApiToken { Name = " mapBOX ", Token = "second-value", UserId = user.Id, User = trackedUser });
            await setup.SaveChangesAsync();
        }

        await using (var migrate = fixture.CreateContext())
        {
            var owner = new PersonalProviderCredentialService(new EphemeralDataProtectionProvider());
            var result = await new LegacyMapboxMigrationService(migrate, owner).MigrateAsync(user.Id);
            Assert.Equal(LegacyMapboxMigrationState.Conflict, result.State);
            Assert.False(result.ProtectedCredentialReady);
        }

        await using var verify = fixture.CreateContext();
        Assert.Equal(2, await verify.ApiTokens.IgnoreQueryFilters().CountAsync(token => token.UserId == user.Id));
    }

    [PostgresFact]
    public async Task GeoapifyConcurrentLastCredit_HasExactlyOneWinnerAcrossContexts()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedVerifiedProfileAsync(user.Id, PersonalLocationProvider.Geoapify,
            PersonalProviderCapability.Geocoding, protection);
        await using (var setup = fixture.CreateContext())
        {
            setup.GeoapifyUsageGuards.Add(new() { UserId = user.Id, Enabled = true, CreditLimit = 1 });
            await setup.SaveChangesAsync();
        }

        await using var firstContext = fixture.CreateContext();
        await using var secondContext = fixture.CreateContext();
        var first = Gate(firstContext, protection).AdmitAsync(user.Id, PersonalProviderCapability.Geocoding,
            PersonalProviderProduct.Geocoding, 1);
        var second = Gate(secondContext, protection).AdmitAsync(user.Id, PersonalProviderCapability.Geocoding,
            PersonalProviderProduct.Geocoding, 1);
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, item => item.Succeeded);
        Assert.Single(results, item => item.Category == PersonalProviderAdmissionCategory.Exhausted);
        await using var verify = fixture.CreateContext();
        Assert.Equal(1, await verify.GeoapifyUsageAdmissions.Where(item => item.UserId == user.Id).SumAsync(item => item.Credits));
    }

    [PostgresFact]
    public async Task MapboxProducts_ExhaustAndRollIndependently()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedVerifiedProfileAsync(user.Id, PersonalLocationProvider.Mapbox,
            PersonalProviderCapability.Geocoding, protection, alsoRouting: true);
        await using (var setup = fixture.CreateContext())
        {
            setup.MapboxProductMeters.AddRange(
                new() { UserId = user.Id, Product = PersonalProviderProduct.PermanentGeocoding, Enabled = true, Limit = 1, CycleStart = new(1970, 1, 1) },
                new() { UserId = user.Id, Product = PersonalProviderProduct.Directions, Enabled = true, Limit = 1, CycleStart = new(1970, 1, 1) });
            await setup.SaveChangesAsync();
        }

        await using var context = fixture.CreateContext();
        var gate = Gate(context, protection);
        Assert.True((await gate.AdmitAsync(user.Id, PersonalProviderCapability.Geocoding,
            PersonalProviderProduct.PermanentGeocoding, 1)).Succeeded);
        Assert.Equal(PersonalProviderAdmissionCategory.Exhausted, (await gate.AdmitAsync(user.Id,
            PersonalProviderCapability.Geocoding, PersonalProviderProduct.PermanentGeocoding, 1)).Category);
        Assert.True((await gate.AdmitAsync(user.Id, PersonalProviderCapability.Routing,
            PersonalProviderProduct.Directions, 1)).Succeeded);
    }

    [PostgresFact]
    public async Task GeoapifySharedPool_RetainsUsageAcrossGuardChangesAndCleansExpiredRows()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedVerifiedProfileAsync(user.Id, PersonalLocationProvider.Geoapify,
            PersonalProviderCapability.Geocoding, protection, alsoRouting: true);
        await using (var setup = fixture.CreateContext())
        {
            setup.GeoapifyUsageGuards.Add(new() { UserId = user.Id, Enabled = true, CreditLimit = 3 });
            await setup.SaveChangesAsync();
            await setup.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO "GeoapifyUsageAdmissions" ("UserId", "Credits", "Product", "AdmittedAt")
                VALUES ({{user.Id}}, 100, 1, clock_timestamp() - interval '25 hours')
                """);
        }
        await using var context = fixture.CreateContext();
        var gate = Gate(context, protection);
        Assert.True((await gate.AdmitAsync(user.Id, PersonalProviderCapability.Geocoding,
            PersonalProviderProduct.Geocoding, 2)).Succeeded);
        Assert.Equal(PersonalProviderAdmissionCategory.Exhausted, (await gate.AdmitAsync(user.Id,
            PersonalProviderCapability.Routing, PersonalProviderProduct.Routing, 2)).Category);

        var guard = await context.GeoapifyUsageGuards.SingleAsync(item => item.UserId == user.Id);
        guard.Enabled = false; guard.CreditLimit = 1; await context.SaveChangesAsync();
        Assert.True((await gate.AdmitAsync(user.Id, PersonalProviderCapability.Routing,
            PersonalProviderProduct.Routing, 1)).Succeeded);
        guard = await context.GeoapifyUsageGuards.SingleAsync(item => item.UserId == user.Id);
        guard.Enabled = true; guard.CreditLimit = 3; await context.SaveChangesAsync();
        Assert.Equal(PersonalProviderAdmissionCategory.Exhausted, (await gate.AdmitAsync(user.Id,
            PersonalProviderCapability.Geocoding, PersonalProviderProduct.Geocoding, 1)).Category);
        guard = await context.GeoapifyUsageGuards.SingleAsync(item => item.UserId == user.Id);
        guard.CreditLimit = 4; await context.SaveChangesAsync();
        Assert.True((await gate.AdmitAsync(user.Id, PersonalProviderCapability.Geocoding,
            PersonalProviderProduct.Geocoding, 1)).Succeeded);
        Assert.Equal(4, await context.GeoapifyUsageAdmissions.Where(item => item.UserId == user.Id).SumAsync(item => item.Credits));
    }

    [PostgresFact]
    public async Task MapboxConcurrentLastDirection_HasExactlyOneWinner()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedVerifiedProfileAsync(user.Id, PersonalLocationProvider.Mapbox,
            PersonalProviderCapability.Routing, protection);
        await using (var setup = fixture.CreateContext())
        {
            setup.MapboxProductMeters.Add(new()
            { UserId = user.Id, Product = PersonalProviderProduct.Directions, Enabled = true, Limit = 1, CycleStart = new(1970, 1, 1) });
            await setup.SaveChangesAsync();
        }
        await using var firstContext = fixture.CreateContext();
        await using var secondContext = fixture.CreateContext();
        var results = await Task.WhenAll(
            Gate(firstContext, protection).AdmitAsync(user.Id, PersonalProviderCapability.Routing, PersonalProviderProduct.Directions, 1),
            Gate(secondContext, protection).AdmitAsync(user.Id, PersonalProviderCapability.Routing, PersonalProviderProduct.Directions, 1));
        Assert.Single(results, item => item.Succeeded);
        Assert.Single(results, item => item.Category == PersonalProviderAdmissionCategory.Exhausted);
    }

    [PostgresFact]
    public async Task StatusReaderUsesStrictDatabaseClockCutoffAndFiveSecondWakeMargin()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedVerifiedProfileAsync(user.Id, PersonalLocationProvider.Geoapify,
            PersonalProviderCapability.Geocoding, protection);
        await using var context = fixture.CreateContext();
        context.GeoapifyUsageGuards.Add(new() { UserId = user.Id, Enabled = true, CreditLimit = 2 });
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "GeoapifyUsageAdmissions" ("UserId", "Credits", "Product", "AdmittedAt")
            VALUES ({{user.Id}}, 100, 1, clock_timestamp() - interval '24 hours'),
                   ({{user.Id}}, 2, 1, clock_timestamp() - interval '23 hours')
            """);

        var status = await Reader(context, protection).InspectPersistentGeocodingAsync(user.Id);

        Assert.Equal(2, status.Usage!.Used);
        Assert.True(status.Exhausted);
        Assert.Equal(status.DatabaseNowUtc.AddHours(-24), status.Usage.RollingCutoff!.Value.UtcDateTime);
        Assert.InRange(status.NextAvailableAt!.Value,
            new DateTimeOffset(status.DatabaseNowUtc.AddHours(1).AddSeconds(4), TimeSpan.Zero),
            new DateTimeOffset(status.DatabaseNowUtc.AddHours(1).AddSeconds(6), TimeSpan.Zero));
    }

    [PostgresFact]
    public async Task StatusReaderUsesDatabaseClockUtcMonthForPermanentMeter()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedVerifiedProfileAsync(user.Id, PersonalLocationProvider.Mapbox,
            PersonalProviderCapability.Geocoding, protection);
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "MapboxProductMeters" ("UserId", "Product", "Enabled", "Limit", "CycleStart", "AdmittedCount")
            VALUES ({{user.Id}}, 3, TRUE, 5, date_trunc('month', clock_timestamp() AT TIME ZONE 'UTC')::date, 4)
            """);

        var status = await Reader(context, protection).InspectPersistentGeocodingAsync(user.Id);

        Assert.Equal(4, status.Usage!.Used);
        Assert.Equal(new DateOnly(status.DatabaseNowUtc.Year, status.DatabaseNowUtc.Month, 1),
            status.Usage.CycleStart);
    }

    [PostgresFact]
    public async Task MapboxVerificationWrite_CannotVerifyReplacementCredential()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await using var context = fixture.CreateContext();
        var owner = new PersonalProviderCredentialService(protection);
        var profile = PersonalLocationProviderProfile.Create(user.Id, PersonalLocationProvider.Mapbox);
        owner.Replace(profile, "generation-one");
        profile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
        profile.GrantPermanentGeocodingConsent(DateTimeOffset.UtcNow);
        context.Add(profile);
        await context.SaveChangesAsync();
        var snapshot = new PersonalProviderAuthoritySnapshot(user.Id, "mapbox", PersonalProviderCapability.Geocoding,
            "generation-one", profile.CredentialGeneration, profile.GeocodingGeneration, 0,
            profile.PermanentGeocodingConsentVersion, profile.PermanentGeocodingConsentedAt,
            profile.PermanentGeocodingConsentCredentialGeneration);

        await using (var replacement = fixture.CreateContext())
        {
            var current = await replacement.PersonalLocationProviderProfiles.SingleAsync(item => item.UserId == user.Id);
            owner.Replace(current, "generation-two");
            current.GrantPermanentGeocodingConsent(DateTimeOffset.UtcNow.AddMinutes(1));
            await replacement.SaveChangesAsync();
        }

        Assert.False(await Gate(context, protection).TryRecordMapboxPermanentVerificationAsync(
            snapshot, PersonalProviderVerification.Verified));
        await using var verify = fixture.CreateContext();
        var retained = await verify.PersonalLocationProviderProfiles.SingleAsync(item => item.UserId == user.Id);
        Assert.Equal(PersonalProviderVerification.Unverified, retained.GeocodingVerification);
        Assert.Null(retained.GeocodingVerifiedCredentialGeneration);
    }

    private async Task SeedVerifiedProfileAsync(string userId, PersonalLocationProvider provider,
        PersonalProviderCapability capability, IDataProtectionProvider protection, bool alsoRouting = false)
    {
        await using var context = fixture.CreateContext();
        var owner = new PersonalProviderCredentialService(protection);
        var profile = PersonalLocationProviderProfile.Create(userId, provider);
        owner.Replace(profile, "test-provider-key");
        if (provider == PersonalLocationProvider.Mapbox && (capability == PersonalProviderCapability.Geocoding || alsoRouting))
            profile.GrantPermanentGeocodingConsent(DateTimeOffset.UtcNow);
        Verify(profile, capability);
        if (alsoRouting) Verify(profile, PersonalProviderCapability.Routing);
        var selection = PersonalLocationProviderSelection.Create(userId);
        selection.Select(capability, provider);
        if (alsoRouting) selection.Select(PersonalProviderCapability.Routing, provider);
        context.AddRange(profile, selection);
        await context.SaveChangesAsync();
    }

    private static void Verify(PersonalLocationProviderProfile profile, PersonalProviderCapability capability)
    {
        profile.SetAuthorization(capability, true);
        if (capability == PersonalProviderCapability.Geocoding)
        {
            profile.GeocodingVerification = PersonalProviderVerification.Verified;
            profile.GeocodingVerifiedCredentialGeneration = profile.CredentialGeneration;
            profile.GeocodingVerifiedConfigurationGeneration = profile.GeocodingGeneration;
        }
        else
        {
            profile.RoutingVerification = PersonalProviderVerification.Verified;
            profile.RoutingVerifiedCredentialGeneration = profile.CredentialGeneration;
            profile.RoutingVerifiedConfigurationGeneration = profile.RoutingGeneration;
        }
    }

    private static PersonalProviderContactGate Gate(Wayfarer.Models.ApplicationDbContext context, IDataProtectionProvider protection)
    {
        var owner = new PersonalProviderCredentialService(protection);
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        return new(context, owner, new LegacyMapboxMigrationService(context, owner), config);
    }

    private static PersonalProviderStatusReader Reader(
        Wayfarer.Models.ApplicationDbContext context, IDataProtectionProvider protection)
        => new(context, new PersonalProviderCredentialService(protection),
            new ConfigurationBuilder().AddInMemoryCollection().Build());
}
