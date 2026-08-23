using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Defines the shared personal-provider authority required by issues 500 through 502.</summary>
public sealed class PersonalLocationProviderFoundationTests
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

        Assert.True(ledger.TryAdmitGeoapify(now, 2_500, 2_499, PersonalProviderProduct.Geocoding));
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
}
