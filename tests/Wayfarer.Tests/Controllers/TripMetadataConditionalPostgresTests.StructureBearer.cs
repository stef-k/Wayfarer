using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services.TripMetadataReceipts;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class TripMetadataConditionalPostgresTests
{
    /// <summary>Protected bearer owners can clear a historical canonical Center and a manual Address from exact stored originals.</summary>
    [PostgresFact]
    public async Task StructureReceipt_BearerEditsCanonicalCenterAndManualAddress()
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        var region = await SeedStructureRegionAsync(db, trip);
        region.Center = new Point(181, 91) { SRID = 4326 };
        region.Name = "Unassigned Places";
        var place = new Place { Id = Guid.NewGuid(), UserId = trip.UserId, RegionId = region.Id, Name = "Place",
            Address = "  Historic address ", Location = new Point(23, 14) { SRID = 4326 }, ResolvedFeatureName = "Feature",
            ResolvedFeatureType = "landmark", AddressEnrichmentProvider = "geoapify", AddressEnrichmentStorageMode = "persistent",
            AddressEnrichedAt = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero) };
        db.Places.Add(place);
        await db.SaveChangesAsync();
        await using var app = await StartAsync(db);
        using var bearer = Bearer(app, trip.UserId);
        const string contract = TripMetadataIdentity.StructureVersion;
        var endpoint = $"/api/trips/{trip.Id}";
        using var ownerReply = await bearer.GetAsync(endpoint + "/planning?contract=" + contract);
        Assert.Equal(HttpStatusCode.OK, ownerReply.StatusCode);
        var storedRegion = (await ownerReply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("regions")[0];
        var center = storedRegion.GetProperty("center");
        var savedCenter = await db.Regions.Where(row => row.Id == region.Id).Select(row => row.Center).SingleAsync();
        Assert.NotNull(savedCenter);
        Assert.Equal(savedCenter.Y, center[1].GetDouble());
        Assert.Equal(savedCenter.X, center[0].GetDouble());
        var storedPlace = storedRegion.GetProperty("places")[0];

        var regionRoute = endpoint + $"/conditional/regions/{region.Id}/receipt";
        var regionIdentity = new TripMetadataIdentity(contract, trip.UserId, "bearer", "region-fields", trip.Id, "", Guid.Empty, default)
            { Target = new("region", region.Id) };
        var regionBody = JsonSerializer.Serialize(new { receiptContract = contract, expected = new { center = Coordinates(center) }, changes = new { center = (object?)null } });
        var regionPermit = await TripMetadataReceiptHttp.IssueTypedAsync(bearer, regionIdentity, regionRoute, regionBody);
        using var regionReply = await TripMetadataReceiptHttp.SendAsync(bearer, HttpMethod.Patch, regionRoute, regionPermit, regionBody);
        var regionHistory = await TripMetadataReceiptHttp.EvidenceAsync(regionReply, regionPermit, "applied");
        Assert.Equal("sha256:8c08745de948fdbd35c1828505a23ea93bcc69d7b210aba8a1008110b4f3e303",
            regionHistory.GetProperty("result").GetProperty("fieldDigests").GetProperty("center").GetString());

        var placeRoute = endpoint + $"/conditional/places/{place.Id}/receipt";
        var placeIdentity = new TripMetadataIdentity(contract, trip.UserId, "bearer", "place-fields", trip.Id, "", Guid.Empty, default)
            { Target = new("place", place.Id) };
        var placeBody = JsonSerializer.Serialize(new { receiptContract = contract, expected = new { regionId = region.Id,
            address = storedPlace.GetProperty("address"), location = Coordinates(storedPlace.GetProperty("location")),
            addressEnrichment = new
            {
                resolvedFeatureName = storedPlace.GetProperty("resolvedFeatureName"),
                resolvedFeatureType = storedPlace.GetProperty("resolvedFeatureType"),
                addressEnrichmentProvider = storedPlace.GetProperty("addressEnrichmentProvider"),
                addressEnrichmentStorageMode = storedPlace.GetProperty("addressEnrichmentStorageMode"),
                addressEnrichedAt = storedPlace.GetProperty("addressEnrichedAt")
            } }, changes = new { address = "" } });
        var placePermit = await TripMetadataReceiptHttp.IssueTypedAsync(bearer, placeIdentity, placeRoute, placeBody);
        using var placeReply = await TripMetadataReceiptHttp.SendAsync(bearer, HttpMethod.Patch, placeRoute, placePermit, placeBody);
        var placeHistory = await TripMetadataReceiptHttp.EvidenceAsync(placeReply, placePermit, "applied");
        Assert.Equal("cleared", placeHistory.GetProperty("result").GetProperty("addressEffect").GetProperty("tupleState").GetString());
        var savedPlace = await db.Places.AsNoTracking().SingleAsync(row => row.Id == place.Id);
        Assert.Equal("", savedPlace.Address);
        Assert.Null(savedPlace.AddressEnrichmentProvider);
        Assert.Equal(place.Location, savedPlace.Location);
    }

    /// <summary>Converts the bearer read pair into the required mutation field names.</summary>
    private static object Coordinates(JsonElement point) => new { longitude = point[0], latitude = point[1] };

    /// <summary>Historical finite originals can be compared even outside the desired coordinate bounds.</summary>
    [Fact]
    public async Task StructureReceipt_BearerHistoricalCenterParserAllowsComparison()
    {
        const string body = """{"expected":{"center":{"latitude":91,"longitude":181}},"changes":{"center":null}}""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var request = await RegionConditionalRequest.ReadAsync(stream, false, default, protectedWrite: true);
        Assert.Equal(new RegionConditionalCenter(91, 181), request.Expected["center"]);
        Assert.Null(request.Changes["center"]);
    }

    /// <summary>Checks the published Region deletion framing and the digest that binds exact polygon rings.</summary>
    [Fact]
    public void StructureReceipt_PublishedRequestAndPolygonDigests()
    {
        var identity = new TripMetadataIdentity(TripMetadataIdentity.StructureVersion, "owner-a", "web", "region-delete",
            Guid.Parse("12345678-1234-4234-8234-123456789abc"), "", Guid.Empty, default)
            { Target = new("region", Guid.Parse("23456789-2345-4345-8345-23456789abcd")) };
        const string payload = """{"receiptContract":"trip-region-place-area-receipt-1","places":"keep","areas":"delete","confirmationToken":"sample-A"}""";
        Assert.Equal("sha256:a379b41c959780929ea3ec64065b980da07db39361f6d6c09f9253eadb7e56c6",
            identity.StructureDigest(Encoding.UTF8.GetBytes(payload)));
        var polygon = new Polygon(new LinearRing([new(0, 0), new(4, 0), new(4, 4), new(0, 4), new(0, 0)]),
            [new LinearRing([new(1, 1), new(1, 2), new(2, 2), new(2, 1), new(1, 1)])]) { SRID = 4326 };
        Assert.Equal("sha256:347fd40450a8dd9bd9dcbd94ddecc37ad0d852b93f544fedd313fec99853c05f",
            TripStructureDigests.Area(new Area { Geometry = polygon }, "geometry"));
    }
}
