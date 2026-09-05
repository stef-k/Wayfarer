using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationProviders;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves Geoapify reverse parsing, request shape, and failure containment with fake HTTP.</summary>
public sealed class GeoapifyReverseGeocodingAdapterTests : Wayfarer.Tests.Infrastructure.TestBase
{
    [Fact]
    public async Task ValidFeatureMapsExactPersistentFields()
    {
        const string json = """
            {"type":"FeatureCollection","features":[{"type":"Feature","properties":{
            "formatted":"12 Main Street, Town","address_line1":"12 Main Street","housenumber":"12",
            "street":"Main Street","postcode":"12345","city":"Town","state":"Region","country":"Country",
            "name":"  Tokyo Tower  ","result_type":"AMENITY"}}]}
            """;
        var handler = new FakeHandler(json);
        var adapter = new GeoapifyReverseGeocodingAdapter(new HttpClient(handler));

        var result = await adapter.ReverseAsync(10.5, 20.25, "secret");

        Assert.True(result.Succeeded);
        Assert.Equal("12 Main Street, Town", result.Value!.FullAddress);
        Assert.Equal("Main Street 12", result.Value.Address);
        Assert.Equal("12 Main Street", result.Value.ProviderAddressLine1);
        Assert.Equal("12", result.Value.AddressNumber);
        Assert.Equal("Main Street", result.Value.StreetName);
        Assert.Equal("12345", result.Value.PostCode);
        Assert.Equal("Town", result.Value.Place);
        Assert.Equal("Region", result.Value.Region);
        Assert.Equal("Country", result.Value.Country);
        Assert.Equal("Tokyo Tower", result.Value.ResolvedFeatureName);
        Assert.Equal("amenity", result.Value.ResolvedFeatureType);
        Assert.Equal("api.geoapify.com", handler.Uri!.Host);
        Assert.Equal("/v1/geocode/reverse", handler.Uri.AbsolutePath);
        Assert.Contains("format=geojson&lang=en&limit=1", handler.Uri.Query, StringComparison.Ordinal);
        // Exercise the actual publication method and database save, retaining the observation.
        using var db = CreateDbContext();
        var capturedAt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var location = new Wayfarer.Models.Location
        {
            UserId = "synthetic", TimeZoneId = "UTC", Timestamp = capturedAt, LocalTimestamp = capturedAt,
            Coordinates = new NetTopologySuite.Geometries.Point(20.25, 10.5), Source = "api-log",
            Provider = "gps", Accuracy = 7, Altitude = 90, Speed = 2, IsUserInvoked = false,
            AppVersion = "released", DeviceModel = "synthetic-device", Notes = "recorded"
        };
        var authority = new PersonalProviderAuthoritySnapshot("synthetic", "geoapify",
            Wayfarer.Models.LocationProviders.PersonalProviderCapability.Geocoding, "synthetic", 1, 1, 1);
        result = result with { Authority = authority };
        var publishedAt = DateTimeOffset.UtcNow;
        Assert.True(result.ApplyTo(location, publishedAt));
        db.Locations.Add(location);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var saved = db.Locations.Single();
        Assert.Equal(("Main Street 12", "12 Main Street, Town", "12 Main Street"),
            (saved.Address, saved.FullAddress, saved.ProviderAddressLine1));
        Assert.Equal(("geoapify", "persistent", publishedAt),
            (saved.ReverseGeocodingProvider, saved.ReverseGeocodingStorageMode, saved.ReverseGeocodedAt));
        Assert.Equal((capturedAt, capturedAt, 20.25, 10.5),
            (saved.Timestamp, saved.LocalTimestamp, saved.Coordinates.X, saved.Coordinates.Y));
        Assert.Equal(("api-log", "gps", 7d, 90d, 2d, false, "released", "synthetic-device", "recorded"),
            (saved.Source, saved.Provider, saved.Accuracy, saved.Altitude, saved.Speed, saved.IsUserInvoked,
                saved.AppVersion, saved.DeviceModel, saved.Notes));
        Assert.Equal(("Tokyo Tower", "amenity"), (saved.ResolvedFeatureName, saved.ResolvedFeatureType));
    }

    [Theory]
    [InlineData("123", "\"amenity\"", null, "amenity")]
    [InlineData("\"bad\\u0001name\"", "\"amenity\"", null, "amenity")]
    [InlineData("\"Name\"", "\"unknown\"", "Name", null)]
    [InlineData("\"Name\"", "\"restaurant\"", "Name", null)]
    public async Task MalformedOptionalMetadataDoesNotRejectValidAddress(
        string name, string resultType, string? expectedName, string? expectedType)
    {
        var json = "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"properties\":{"+
            "\"formatted\":\"12 Main Street, Town\",\"name\":" + name + ",\"result_type\":" + resultType + "}}]}";

        var result = await new GeoapifyReverseGeocodingAdapter(new HttpClient(new FakeHandler(json)))
            .ReverseAsync(10, 20, "secret");

        Assert.True(result.Succeeded);
        Assert.Equal(expectedName, result.Value!.ResolvedFeatureName);
        Assert.Equal(expectedType, result.Value.ResolvedFeatureType);
    }

    [Theory]
    [InlineData("{\"type\":\"FeatureCollection\",\"features\":[]}")]
    [InlineData("{\"type\":\"FeatureCollection\"}")]
    [InlineData("{\"type\":\"FeatureCollection\",\"features\":[{\"properties\":{}}]}")]
    public async Task EmptyOrMalformedResponseNeverProducesPersistenceAuthority(string json)
    {
        var result = await new GeoapifyReverseGeocodingAdapter(new HttpClient(new FakeHandler(json)))
            .ReverseAsync(10, 20, "secret");

        Assert.False(result.Succeeded);
        Assert.Null(result.Authority);
    }

    /// <summary>Contains a non-string GeoJSON envelope type as a bounded invalid response.</summary>
    [Fact]
    public async Task NonStringEnvelopeTypeReturnsInvalidResponse()
    {
        const string json = """{"type":42,"features":[]}""";

        var result = await new GeoapifyReverseGeocodingAdapter(new HttpClient(new FakeHandler(json)))
            .ReverseAsync(10, 20, "secret");

        Assert.False(result.Succeeded);
        Assert.Equal(ReverseGeocodingCategory.InvalidResponse, result.Category);
        Assert.Null(result.Authority);
    }

    /// <summary>Administrative levels and optional scalars never invent a settlement or street.</summary>
    [Theory]
    [InlineData("\"result_type\":\"building\",\"name\":\"Tower\",\"street\":\"Οδός\",\"housenumber\":\"10-12\",\"city\":\"City\"", "Οδός 10-12", "10-12", "City")]
    [InlineData("\"street\":\"Lane\",\"housenumber\":\"001\",\"city\":\" \" ,\"town\":\"Town\",\"village\":\"Village\"", "Lane 001", "001", "Town")]
    [InlineData("\"result_type\":\"amenity\",\"name\":\"Cafe\",\"village\":\"Village\",\"postcode\":\"00123\"", "", "", "Village")]
    [InlineData("\"result_type\":\"amenity\",\"name\":\"Hotel\",\"state\":\"State\",\"country\":\"Country\"", "", "", null)]
    [InlineData("\"municipality\":\"Municipality\",\"county\":\"County\",\"state_district\":\"District\"", "", "", null)]
    [InlineData("\"street\":12,\"housenumber\":10,\"postcode\":123,\"city\":{},\"town\":false,\"village\":null", "", "", null)]
    [InlineData("\"housenumber\":\"001\",\"address_line1\":42", "", "001", null)]
    [InlineData("\"address_line1\":\"   \" ,\"street\":null,\"state\":[]", "", "", null)]
    public async Task PartialResultsRetainOnlySuppliedStructuredStrings(string properties, string address,
        string number, string? place)
    {
        var json = "{\"type\":\"FeatureCollection\",\"features\":[{\"properties\":{\"formatted\":\"Display only\"," + properties + "}}]}";
        var result = await new GeoapifyReverseGeocodingAdapter(new HttpClient(new FakeHandler(json)))
            .ReverseAsync(10, 20, "synthetic");
        Assert.True(result.Succeeded);
        Assert.Equal(address, result.Value!.Address);
        Assert.Equal(number, result.Value.AddressNumber);
        Assert.Equal(place, result.Value.Place);
        Assert.Equal(properties.Contains("\"state\":\"State\"") ? "State" : null, result.Value.Region);
        Assert.Null(result.Value.ProviderAddressLine1);
        Assert.Equal(properties.Contains("00123") ? "00123" : "", result.Value.PostCode);
        Assert.Equal("Display only", result.Value.FullAddress);
    }

    /// <summary>The independently supplied line retains its existing bound and display-only admission.</summary>
    [Fact]
    public async Task ProviderLineIsBoundedWithoutInventingAStreet()
    {
        var line = new string('Å', 501);
        var json = "{\"type\":\"FeatureCollection\",\"features\":[{\"properties\":{\"address_line1\":\"" + line + "\"}}]}";
        var result = await new GeoapifyReverseGeocodingAdapter(new HttpClient(new FakeHandler(json)))
            .ReverseAsync(10, 20, "synthetic");
        Assert.True(result.Succeeded);
        Assert.Equal(line[..500], result.Value!.ProviderAddressLine1);
        Assert.Equal(line[..500], result.Value.FullAddress);
        Assert.Empty(result.Value.Address);
    }

    private sealed class FakeHandler(string json) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
