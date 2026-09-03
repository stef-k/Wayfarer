using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Exercises additive Mobile discovery from personal provider authority only.</summary>
public sealed class MobileRoutingProfileDiscoveryTests : TestBase
{
    [Fact]
    public async Task AvailableReturnsFiveModesAndOnlyExactReleasedClientProfiles()
    {
        var service = CreateConfiguredService();
        var walk = AddProfile("walk", "Walking", 2);
        AddProfile("custom", "Walk", 1);
        AddProfile("Walk", "Translated", 0);
        await Db.SaveChangesAsync();
        var reads = 0;
        service.CredentialReadOverride = () => { reads++; return true; };

        var result = await service.DiscoverAsync("owner", default);

        Assert.Equal("available", result.Outcome);
        Assert.Equal("geoapify", result.Provider);
        Assert.Equal(["walk", "bicycle", "motorcycle", "drive", "bus"],
            result.Modes.Select(item => item.Key));
        Assert.Contains(result.Profiles, item => item.TransportProfileId == walk.Id);
        Assert.DoesNotContain(result.Profiles, item => item.ModeKey is "custom" or "Walk");
        Assert.True(DiscoveryCatalogIdentity.IsValid(result.DiscoveryCatalogIdentity));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task PersonalAuthorityDriftAfterReadReturnsNoCatalog()
    {
        var service = CreateConfiguredService();
        AddProfile("walk", "Walk", 0);
        await Db.SaveChangesAsync();
        service.AfterCredentialReadAsync = async token =>
        {
            var profile = Db.PersonalLocationProviderProfiles.Single();
            profile.RoutingGeneration++;
            await Db.SaveChangesAsync(token);
        };

        var result = await service.DiscoverAsync("owner", default);

        Assert.Equal("temporarily-unavailable", result.Outcome);
        Assert.Empty(result.Profiles);
        Assert.Null(result.DiscoveryCatalogIdentity);
    }

    [Fact]
    public async Task ReleasedProfileCatalogDriftAfterReadReturnsNoCatalog()
    {
        var service = CreateConfiguredService();
        var walk = AddProfile("walk", "Walk", 0);
        await Db.SaveChangesAsync();
        service.AfterCredentialReadAsync = async token =>
        {
            walk.Key = "custom";
            await Db.SaveChangesAsync(token);
        };

        var result = await service.DiscoverAsync("owner", default);

        Assert.Equal("temporarily-unavailable", result.Outcome);
        Assert.Empty(result.Profiles);
    }

    [Fact]
    public async Task GlobalAndAdministratorConfigurationDoNotParticipate()
    {
        var service = CreateConfiguredService();
        AddProfile("car", "Car", 0);
        Db.Add(new ApplicationSettings { Id = 1, ExternalRouteGenerationEnabled = false });
        Db.Add(new RoutingProviderConfiguration
        {
            Id = Guid.NewGuid(), DisplayName = "Disabled admin provider", Enabled = false,
            AdapterType = RoutingAdapterType.MapboxDirections
        });
        await Db.SaveChangesAsync();

        var result = await service.DiscoverAsync("owner", default);

        Assert.Equal("available", result.Outcome);
        Assert.Contains(result.Profiles, item => item.ModeKey == "car");
    }

    [Fact]
    public async Task NoPersonalSelectionReturnsBoundedNoAuthority()
    {
        var service = CreateConfiguredService();
        Db.PersonalLocationProviderSelections.Single().RoutingProviderKey = null;
        await Db.SaveChangesAsync();

        var result = await service.DiscoverAsync("owner", default);

        Assert.Equal("no-authority", result.Outcome);
        Assert.Empty(result.Profiles);
        Assert.Empty(result.Modes);
    }

    private ApplicationDbContext Db { get; set; } = null!;

    private MobileRoutingProfileDiscoveryService CreateConfiguredService()
    {
        Db = CreateDbContext();
        var credentials = new PersonalProviderCredentialService(new EphemeralDataProtectionProvider());
        var profile = PersonalLocationProviderProfile.Create("owner", PersonalLocationProvider.Geoapify);
        credentials.Replace(profile, "secret");
        profile.RoutingAuthorized = true;
        profile.RoutingVerification = PersonalProviderVerification.Verified;
        profile.RoutingVerifiedCredentialGeneration = profile.CredentialGeneration;
        profile.RoutingVerifiedConfigurationGeneration = profile.RoutingGeneration;
        Db.AddRange(profile, new PersonalLocationProviderSelection
        { UserId = "owner", RoutingProviderKey = "geoapify" });
        Db.SaveChanges();
        return new(Db, credentials);
    }

    private TransportProfile AddProfile(string key, string label, int order)
    {
        var profile = new TransportProfile
        { Id = Guid.NewGuid(), Key = key, Label = label, Category = "test", SortOrder = order, IsActive = true };
        Db.Add(profile);
        return profile;
    }
}
