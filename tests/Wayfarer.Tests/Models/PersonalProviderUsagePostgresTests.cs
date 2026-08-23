using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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

    private async Task SeedVerifiedProfileAsync(string userId, PersonalLocationProvider provider,
        PersonalProviderCapability capability, IDataProtectionProvider protection, bool alsoRouting = false)
    {
        await using var context = fixture.CreateContext();
        var owner = new PersonalProviderCredentialService(protection);
        var profile = PersonalLocationProviderProfile.Create(userId, provider);
        owner.Replace(profile, "test-provider-key");
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
}
