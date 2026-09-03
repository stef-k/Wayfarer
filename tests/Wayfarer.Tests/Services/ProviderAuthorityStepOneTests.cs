using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Areas.User.Controllers;
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
    public async Task WebProposalRequest_RequiresExplicitModeBeforeGeneration()
    {
        var controller = new ExternalRouteProposalsController(
            new ExternalRouteProposalGenerator(() => new ApplicationSettings { ExternalRouteGenerationEnabled = true }))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "user")], "test"))
            } }
        };

        var result = await controller.Generate(Guid.NewGuid(), Guid.NewGuid(),
            new ExternalRouteGenerationRequest("token"), default);

        var rejected = Assert.IsType<UnprocessableEntityObjectResult>(result);
        Assert.Equal("provider-mode-required", Assert.IsType<ExternalRouteErrorDto>(rejected.Value).Code);
    }

    [Fact]
    public void ProviderSettingsView_DoesNotExposeInternalAuthorizationOrActivationCheckboxes()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Areas", "User", "Views",
            "LocationProviderSettings", "Index.cshtml"));

        Assert.DoesNotContain("GeocodingAuthorized", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RoutingAuthorized", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ActiveForGeocoding", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ActiveForRouting", source, StringComparison.Ordinal);
        Assert.Contains("ChooseProvider", source, StringComparison.Ordinal);
        Assert.Contains("for=\"geoapify-credential\"", source, StringComparison.Ordinal);
        Assert.Contains("id=\"geoapify-credential\"", source, StringComparison.Ordinal);
        Assert.Contains("mapbox-directions-guard-enabled", source, StringComparison.Ordinal);
        Assert.Contains("{profile.ProviderKey}-guard-enabled", source, StringComparison.Ordinal);

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

    [Fact]
    public async Task SetupChoice_RejectsVerifiedButUnreadableCredentialWithoutMutation()
    {
        await using var db = CreateDbContext();
        var storedCredentials = new PersonalProviderCredentialService(new EphemeralDataProtectionProvider());
        var profile = VerifiedGeoapify("user", storedCredentials);
        db.Add(profile);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var setup = new PersonalProviderSetupService(db,
            new PersonalProviderCredentialService(new EphemeralDataProtectionProvider()));

        var result = await setup.ChooseAsync("user", PersonalProviderCapability.Routing,
            PersonalLocationProvider.Geoapify, default);

        Assert.Equal(ProviderChoiceResult.NotVerified, result);
        Assert.Empty(db.PersonalLocationProviderSelections);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task SettingsProjection_BlocksSelectedUnreadableOrRevokedGeoapifyAuthority(
        bool revoked, bool useMatchingProtector)
    {
        await using var db = CreateDbContext();
        var storedCredentials = new PersonalProviderCredentialService(new EphemeralDataProtectionProvider());
        var presentationCredentials = useMatchingProtector
            ? storedCredentials : new PersonalProviderCredentialService(new EphemeralDataProtectionProvider());
        var profile = VerifiedGeoapify("user", storedCredentials);
        if (revoked) profile.RevokedAt = DateTimeOffset.UtcNow;
        var selection = PersonalLocationProviderSelection.Create("user");
        selection.Select(PersonalProviderCapability.Geocoding, PersonalLocationProvider.Geoapify);
        selection.Select(PersonalProviderCapability.Routing, PersonalLocationProvider.Geoapify);
        db.AddRange(profile, selection);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var controller = new LocationProviderSettingsController(db, presentationCredentials,
            new LegacyMapboxMigrationService(db, presentationCredentials), null!);

        var model = await controller.BuildAsync("user", default);

        var geoapify = Assert.Single(model.Profiles, item => item.ProviderKey == "geoapify");
        Assert.False(geoapify.GeocodingEligible);
        Assert.False(geoapify.RoutingEligible);
        Assert.Contains("Replace the credential and verify again", model.GeocodingStatus);
        Assert.Contains("Replace the credential and verify again", model.RoutingStatus);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task SettingsProjection_OffersReadableCurrentVerifiedGeoapifyAsReady()
    {
        await using var db = CreateDbContext();
        var credentials = new PersonalProviderCredentialService(new EphemeralDataProtectionProvider());
        var profile = VerifiedGeoapify("user", credentials);
        var selection = PersonalLocationProviderSelection.Create("user");
        selection.Select(PersonalProviderCapability.Geocoding, PersonalLocationProvider.Geoapify);
        selection.Select(PersonalProviderCapability.Routing, PersonalLocationProvider.Geoapify);
        db.AddRange(profile, selection);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var controller = new LocationProviderSettingsController(db, credentials,
            new LegacyMapboxMigrationService(db, credentials), null!);

        var model = await controller.BuildAsync("user", default);

        var geoapify = Assert.Single(model.Profiles, item => item.ProviderKey == "geoapify");
        Assert.True(geoapify.GeocodingEligible);
        Assert.True(geoapify.RoutingEligible);
        Assert.Equal("Ready with geoapify.", model.GeocodingStatus);
        Assert.Equal("Ready with geoapify.", model.RoutingStatus);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    private static PersonalLocationProviderProfile VerifiedGeoapify(
        string userId, PersonalProviderCredentialService credentials)
    {
        var profile = PersonalLocationProviderProfile.Create(userId, PersonalLocationProvider.Geoapify);
        credentials.Replace(profile, "credential");
        profile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
        profile.SetAuthorization(PersonalProviderCapability.Routing, true);
        credentials.RecordVerification(profile, PersonalProviderCapability.Geocoding, PersonalProviderVerification.Verified);
        credentials.RecordVerification(profile, PersonalProviderCapability.Routing, PersonalProviderVerification.Verified);
        return profile;
    }

    private static TransportProfile Profile(string key) => new()
    { Id = Guid.NewGuid(), Key = key, Label = key, Category = "other", IsActive = true };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Wayfarer.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
