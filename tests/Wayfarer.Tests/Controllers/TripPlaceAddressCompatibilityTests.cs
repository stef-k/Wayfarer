using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationProviders;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Proves Location street mapping does not change the shared Trip reverse-geocoding consumer.</summary>
public sealed class TripPlaceAddressCompatibilityTests : TripEditorPlaceControllerTestBase
{
    [Fact]
    public async Task GeoapifyTripPlaceKeepsFullAddressPreference()
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var region = trip.Regions.Single(item => item.Name == "Athens");
        var credentials = new PersonalProviderCredentialService(new EphemeralDataProtectionProvider());
        var profile = PersonalLocationProviderProfile.Create("owner-user", PersonalLocationProvider.Geoapify);
        credentials.Replace(profile, "synthetic");
        profile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
        credentials.RecordVerification(profile, PersonalProviderCapability.Geocoding, PersonalProviderVerification.Verified);
        db.Add(profile);
        db.Add(new PersonalLocationProviderSelection { UserId = "owner-user", GeocodingProviderKey = "geoapify" });
        await db.SaveChangesAsync();
        var gate = new PersonalProviderContactGate(db, credentials, new LegacyMapboxMigrationService(db, credentials),
            new ConfigurationBuilder().Build());
        var reverse = new ReverseGeocodingService(new HttpClient(new Handler()), NullLogger<BaseApiController>.Instance, gate, db);
        var controller = BuildController(db, reverse);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var response = await SendJson(controller, c => c.CreatePlace(trip.Id, region.Id, CancellationToken.None),
            ValidCreateBody("Geo", reverseGeocode: true));

        var envelope = AssertMutation<EditorPlaceDto>(response);
        Assert.Empty(envelope.Warnings);
        Assert.Equal("Hotel, Display Town", envelope.Data.Address);
        Assert.Equal("Hotel, Display Town", db.Places.Single(item => item.Id == envelope.Data.Id).Address);
        Assert.Null(typeof(Wayfarer.Models.Place).GetProperty("ProviderAddressLine1"));
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"type":"FeatureCollection","features":[{"properties":{"formatted":"Hotel, Display Town","address_line1":"Hotel","street":"Street","housenumber":"10-12"}}]}""")
            });
    }
}
