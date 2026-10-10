using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Wayfarer.Models;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Proves editor Place address compatibility, canonical metadata transitions and provider replacement.</summary>
public sealed class TripPlaceAddressCompatibilityTests : TripEditorPlaceControllerTestBase
{
    private static readonly DateTimeOffset OriginalEnrichedAt = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The real authenticated, antiforgery-protected PUT retains its response shape and returns stored metadata.</summary>
    [Fact]
    public async Task RoutedEditorPutReturnsCommittedMetadataInExistingEnvelope()
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var place = SeedMetadata(trip, "Old address");
        db.Users.Add(new ApplicationUser { Id = "owner-user", UserName = "Owner", DisplayName = "Owner", IsActive = true });
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            // This existing host omits startup-only thumbnail storage and job scheduling.
            services.AddSingleton(Mock.Of<ITripMapThumbnailGenerator>());
            services.AddSingleton(Mock.Of<ICacheWarmupScheduler>());
        });
        using var client = app.GetTestClient();
        var scheme = IdentityConstants.ApplicationScheme;
        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(scheme);
        var ticket = new AuthenticationTicket(BuildHttpContextWithUser("owner-user", "User").User,
            new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10) }, scheme);
        var authCookie = options.Cookie.Name + "=" + options.TicketDataFormat.Protect(ticket);
        client.DefaultRequestHeaders.Add("Cookie", authCookie);
        var token = await IdentityRouteHost.AntiforgeryAsync(client);
        var antiforgeryCookie = client.DefaultRequestHeaders.GetValues("Cookie").Single();
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", authCookie + "; " + antiforgeryCookie);
        client.DefaultRequestHeaders.Add("RequestVerificationToken", token);

        using var response = await client.PutAsync($"/api/trips/{trip.Id}/editor/places/{place.Id}",
            new StringContent(UpdateBody(place.RegionId, "Changed", 37, 23, false), System.Text.Encoding.UTF8, "application/json"));

        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        Assert.Equal(new[] { "affected", "data", "deletedIds", "success", "warnings" }, root.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("Changed", root.GetProperty("data").GetProperty("address").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("data").GetProperty("resolvedFeatureName").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("data").GetProperty("resolvedFeatureType").ValueKind);
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
        Assert.Equal("Changed", (await db.Places.AsNoTracking().SingleAsync(item => item.Id == place.Id)).Address);
    }

    /// <summary>Preserves compatible manual/fallback values and clears all provenance when either value changes.</summary>
    [Theory]
    [InlineData("Old address", "Changed", 37d, 23d, false, false)]
    [InlineData("Old address", "Old address", 11d, 22d, false, false)]
    [InlineData("Old address", "", 37d, 23d, false, false)]
    [InlineData("Old address", "Old address", null, null, false, false)]
    [InlineData(" Old address ", "Old address", 37d, 23d, false, true)]
    [InlineData("Old address", " Old address ", 37d, 23d, false, true)]
    [InlineData(null, " ", 37d, 23d, false, true)]
    [InlineData("", null, 37d, 23d, false, true)]
    [InlineData("Old address", "Changed", 37d, 23d, true, false)]
    [InlineData("Old address", "Old address", 37d, 23d, true, true)]
    public async Task ManualOrUnavailableUpdateUsesCanonicalCompatibility(
        string? originalAddress, string? submittedAddress, double? latitude, double? longitude,
        bool reverseGeocode, bool preserve)
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var place = SeedMetadata(trip, originalAddress);
        await db.SaveChangesAsync();
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var envelope = AssertMutation<EditorPlaceDto>(await SendJson(controller,
            c => c.UpdatePlace(trip.Id, place.Id, CancellationToken.None),
            UpdateBody(place.RegionId, submittedAddress, latitude, longitude, reverseGeocode)));

        var stored = await db.Places.AsNoTracking().SingleAsync(item => item.Id == place.Id);
        Assert.Equal(submittedAddress?.Trim() ?? "", stored.Address);
        Assert.Equal(preserve ? "Old feature" : null, stored.ResolvedFeatureName);
        Assert.Equal(preserve ? "building" : null, stored.ResolvedFeatureType);
        Assert.Equal(preserve ? "geoapify" : null, stored.AddressEnrichmentProvider);
        Assert.Equal(preserve ? "persistent" : null, stored.AddressEnrichmentStorageMode);
        Assert.Equal(preserve ? OriginalEnrichedAt : (DateTimeOffset?)null, stored.AddressEnrichedAt);
        Assert.Equal(stored.ResolvedFeatureName, envelope.Data.ResolvedFeatureName);
        Assert.Equal(stored.ResolvedFeatureType, envelope.Data.ResolvedFeatureType);
        Assert.Equal("<p>Notes</p>", stored.Notes);
        Assert.Equal(reverseGeocode ? 1 : 0, envelope.Warnings.Count);
        if (reverseGeocode) Assert.Equal("reverse-geocode-unavailable", envelope.Warnings.Single().Code);
    }

    /// <summary>Region-only and other-field edits retain canonical metadata and its original timestamp.</summary>
    [Fact]
    public async Task RegionOnlyUpdatePreservesMetadata()
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var place = SeedMetadata(trip, "Old address");
        await db.SaveChangesAsync();
        var target = trip.Regions.Single(region => region.Name == "Thessaloniki");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var envelope = AssertMutation<EditorPlaceDto>(await SendJson(controller,
            c => c.UpdatePlace(trip.Id, place.Id, CancellationToken.None),
            UpdateBody(target.Id, "Old address", 37, 23, false)));

        Assert.Equal(target.Id, envelope.Data.RegionId);
        var stored = await db.Places.AsNoTracking().SingleAsync(item => item.Id == place.Id);
        Assert.Equal("Old feature", stored.ResolvedFeatureName);
        Assert.Equal(OriginalEnrichedAt, stored.AddressEnrichedAt);
    }

    /// <summary>Successful enrichment replaces optional nulls and full provenance even with unchanged address text.</summary>
    [Theory]
    [InlineData(PersonalLocationProvider.Geoapify, "New feature", "amenity")]
    [InlineData(PersonalLocationProvider.Geoapify, null, null)]
    [InlineData(PersonalLocationProvider.Mapbox, null, null)]
    public async Task SuccessfulUpdateReplacesCompleteTuple(
        PersonalLocationProvider provider, string? featureName, string? featureType)
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var place = SeedMetadata(trip, "Old address");
        var credentials = Wayfarer.Tests.Infrastructure.CredentialTestFactory.Create(new EphemeralDataProtectionProvider());
        var profile = PersonalLocationProviderProfile.Create("owner-user", provider);
        credentials.Replace(profile, "synthetic");
        profile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
        if (provider == PersonalLocationProvider.Mapbox) profile.GrantPermanentGeocodingConsent(DateTimeOffset.UtcNow);
        credentials.RecordVerification(profile, PersonalProviderCapability.Geocoding, PersonalProviderVerification.Verified);
        db.Add(profile);
        db.Add(new PersonalLocationProviderSelection { UserId = "owner-user", GeocodingProviderKey = profile.ProviderKey });
        await db.SaveChangesAsync();
        var payload = provider == PersonalLocationProvider.Geoapify
            ? JsonSerializer.Serialize(new { type = "FeatureCollection", features = new[] { new { properties = new { formatted = "Old address", name = featureName, result_type = featureType } } } })
            : """{"type":"FeatureCollection","features":[{"type":"Feature","id":"street.1","properties":{"feature_type":"street","full_address":"Old address"}}]}""";
        using var handler = new Handler(payload);
        using var client = new HttpClient(handler);
        var gate = new PersonalProviderContactGate(db, credentials, new LegacyMapboxMigrationService(db, credentials), new ConfigurationBuilder().Build());
        var controller = BuildController(db, new ReverseGeocodingService(client, NullLogger<BaseApiController>.Instance, gate, db));
        ConfigureControllerWithUserRole(controller, "owner-user");
        var before = DateTimeOffset.UtcNow;

        var envelope = AssertMutation<EditorPlaceDto>(await SendJson(controller,
            c => c.UpdatePlace(trip.Id, place.Id, CancellationToken.None),
            UpdateBody(place.RegionId, "Old address", 37, 23, true)));

        var stored = await db.Places.AsNoTracking().SingleAsync(item => item.Id == place.Id);
        Assert.Equal("Old address", stored.Address);
        Assert.Equal(featureName, stored.ResolvedFeatureName);
        Assert.Equal(featureType, stored.ResolvedFeatureType);
        Assert.Equal(profile.ProviderKey, stored.AddressEnrichmentProvider);
        Assert.Equal(provider == PersonalLocationProvider.Geoapify ? "persistent" : "permanent", stored.AddressEnrichmentStorageMode);
        Assert.InRange(stored.AddressEnrichedAt!.Value, before, DateTimeOffset.UtcNow);
        Assert.Equal(stored.ResolvedFeatureName, envelope.Data.ResolvedFeatureName);
        Assert.Equal(stored.ResolvedFeatureType, envelope.Data.ResolvedFeatureType);
        Assert.Empty(envelope.Warnings);
        Assert.Equal(1, handler.Requests);
    }

    /// <summary>Omitting the editor directive leaves the existing legacy lifecycle behavior unchanged.</summary>
    [Fact]
    public async Task LegacyUpdateWithoutDirectivePreservesMetadata()
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var place = SeedMetadata(trip, "Old address");
        await db.SaveChangesAsync();
        var lifecycle = new PlaceRegionLifecycleService(db, new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider()));

        var result = await lifecycle.UpdatePlaceAsync(trip.Id, place.Id, "owner-user",
            new PlaceLifecycleUpdate(place.RegionId, "Legacy", "", "Changed", "marker", "bg-blue", place.Location), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("Old feature", result.Place!.ResolvedFeatureName);
        Assert.Equal(OriginalEnrichedAt, result.Place.AddressEnrichedAt);
    }

    [Fact]
    public async Task GeoapifyTripPlaceKeepsFullAddressPreference()
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var region = trip.Regions.Single(item => item.Name == "Athens");
        var credentials = Wayfarer.Tests.Infrastructure.CredentialTestFactory.Create(new EphemeralDataProtectionProvider());
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

    /// <summary>Seeds an existing coherent tuple before exercising the editor mutation seam.</summary>
    private static Place SeedMetadata(Trip trip, string? address)
    {
        var place = trip.Regions.Single(region => region.Name == "Athens").Places.Single();
        place.Address = address;
        place.ResolvedFeatureName = "Old feature";
        place.ResolvedFeatureType = "building";
        place.AddressEnrichmentProvider = "geoapify";
        place.AddressEnrichmentStorageMode = "persistent";
        place.AddressEnrichedAt = OriginalEnrichedAt;
        return place;
    }

    /// <summary>Builds a complete editor draft using only existing public fields.</summary>
    private static string UpdateBody(Guid regionId, string? address, double? latitude, double? longitude, bool reverseGeocode) =>
        JsonSerializer.Serialize(new
        {
            regionId, name = "Updated", notesHtml = "<p>Notes</p>", address,
            location = latitude.HasValue ? new { latitude, longitude } : null,
            iconName = "marker", markerColor = "bg-blue", reverseGeocode
        });

    /// <summary>Returns provider results without contacting an external service.</summary>
    private sealed class Handler(string payload = """{"type":"FeatureCollection","features":[{"properties":{"formatted":"Hotel, Display Town","address_line1":"Hotel","street":"Street","housenumber":"10-12"}}]}""") : HttpMessageHandler
    {
        /// <summary>Counts actual admitted outbound requests.</summary>
        public int Requests { get; private set; }

        /// <summary>Supplies a synthetic response to the production adapter.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload)
            });
        }
    }
}
