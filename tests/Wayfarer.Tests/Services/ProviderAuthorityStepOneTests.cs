using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves the provider-native catalog, compatibility adapter, and setup transitions for issue 538 step 1.</summary>
public sealed class ProviderAuthorityStepOneTests : TestBase
{
    [Fact]
    public void DirectionsCatalog_ExposesOnlyFiveGeoapifyModesAndNoMapboxModes()
    {
        Assert.Equal(["walk", "bicycle", "motorcycle", "drive", "bus"],
            ProviderDirectionsCatalog.For("geoapify").Select(item => item.Key));
        Assert.Empty(ProviderDirectionsCatalog.For("mapbox"));
        Assert.False(ProviderDirectionsCatalog.TryParse("geoapify", "Walk", out _));
    }

    [Theory]
    [InlineData("walk", "walk")]
    [InlineData("bicycle", "bicycle")]
    [InlineData("bike", "bicycle")]
    [InlineData("car", "drive")]
    [InlineData("bus", "bus")]
    public void ReleasedMobileAdapter_MapsOnlyExactReviewedStableKeys(string key, string expected)
    {
        var profile = Profile(key);

        Assert.True(ReleasedMobileDirectionsCompatibility.TryMap(profile, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("Walk")]
    [InlineData("walking")]
    [InlineData(" bicycle ")]
    [InlineData("motorcycle")]
    [InlineData("custom")]
    public void ReleasedMobileAdapter_RejectsCaseNearMatchAndFreeFormKeys(string key)
    {
        var profile = Profile(key);
        profile.Label = "walk";
        profile.Category = "car";

        Assert.False(ReleasedMobileDirectionsCompatibility.TryMap(profile, out _));
    }

    [Fact]
    public async Task SetupTransitions_AuthorizeVerifySelectAndDisableOnlyOneCapability()
    {
        await using var db = CreateDbContext();
        var credentials = new PersonalProviderCredentialService(new EphemeralDataProtectionProvider());
        var setup = new PersonalProviderSetupService(db, credentials);
        var profile = PersonalLocationProviderProfile.Create("user", PersonalLocationProvider.Geoapify);
        credentials.Replace(profile, "credential");
        db.Add(profile);
        await db.SaveChangesAsync();

        Assert.True(await setup.AuthorizeVerificationAsync("user", PersonalLocationProvider.Geoapify,
            PersonalProviderCapability.Routing, default));
        Assert.True(profile.RoutingAuthorized);
        Assert.False(profile.GeocodingAuthorized);
        credentials.RecordVerification(profile, PersonalProviderCapability.Routing, PersonalProviderVerification.Verified);
        await db.SaveChangesAsync();

        Assert.Equal(ProviderChoiceResult.Success, await setup.ChooseAsync("user",
            PersonalProviderCapability.Routing, PersonalLocationProvider.Geoapify, default));
        Assert.Equal("geoapify", (await db.PersonalLocationProviderSelections.SingleAsync()).RoutingProviderKey);

        Assert.Equal(ProviderChoiceResult.Success, await setup.ChooseAsync("user",
            PersonalProviderCapability.Routing, null, default));
        Assert.Null((await db.PersonalLocationProviderSelections.SingleAsync()).RoutingProviderKey);
        Assert.False(profile.RoutingAuthorized);
        Assert.Equal(PersonalProviderVerification.Unverified, profile.RoutingVerification);
        Assert.False(profile.GeocodingAuthorized);
    }

    [Fact]
    public async Task CredentialReplacement_ClearsBothSelectionsAndAuthorityWithoutContact()
    {
        await using var db = CreateDbContext();
        var credentials = new PersonalProviderCredentialService(new EphemeralDataProtectionProvider());
        var setup = new PersonalProviderSetupService(db, credentials);
        var profile = PersonalLocationProviderProfile.Create("user", PersonalLocationProvider.Geoapify);
        credentials.Replace(profile, "old");
        profile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
        profile.SetAuthorization(PersonalProviderCapability.Routing, true);
        credentials.RecordVerification(profile, PersonalProviderCapability.Geocoding, PersonalProviderVerification.Verified);
        credentials.RecordVerification(profile, PersonalProviderCapability.Routing, PersonalProviderVerification.Verified);
        var selection = PersonalLocationProviderSelection.Create("user");
        selection.Select(PersonalProviderCapability.Geocoding, PersonalLocationProvider.Geoapify);
        selection.Select(PersonalProviderCapability.Routing, PersonalLocationProvider.Geoapify);
        db.AddRange(profile, selection);
        await db.SaveChangesAsync();

        await setup.ReplaceCredentialAsync("user", PersonalLocationProvider.Geoapify, "new", default);

        Assert.False(profile.GeocodingAuthorized);
        Assert.False(profile.RoutingAuthorized);
        Assert.Equal(PersonalProviderVerification.Unverified, profile.GeocodingVerification);
        Assert.Equal(PersonalProviderVerification.Unverified, profile.RoutingVerification);
        Assert.Null(selection.GeocodingProviderKey);
        Assert.Null(selection.RoutingProviderKey);
        Assert.Equal("new", credentials.Read(profile).Credential);
    }

    private static TransportProfile Profile(string key) => new()
    { Id = Guid.NewGuid(), Key = key, Label = key, Category = "other", IsActive = true };
}
