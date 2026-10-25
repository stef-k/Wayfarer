using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Unsupported unconditional Place PUT cannot bypass selected structural or scalar protection.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class ApiPlacePartialUpdatePostgresTests(PostgresImportTestFixture fixture) : TestBase
{
    /// <summary>All formerly admitted scalar, coordinate, move and scalar-order intentions reject without any footprint.</summary>
    [PostgresTheory]
    [InlineData("notes")]
    [InlineData("coordinates")]
    [InlineData("move")]
    [InlineData("order")]
    public async Task UnconditionalPutRequiresOriginalsAndPreservesState(string operation)
    {
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Protected Trip" }; fixture.RegisterTrip(trip.Id);
        var region = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id, Name = "Region", DisplayOrder = 1 };
        var place = new Place { Id = Guid.NewGuid(), UserId = user.Id, Region = region, RegionId = region.Id, Name = "Original", Notes = "Notes",
            Address = "Address", Location = new Point(22, 14) { SRID = 4326 }, DisplayOrder = null };
        await using var db = fixture.CreateContext(); db.Places.Add(place);
        db.ApiTokens.Add(new ApiToken { UserId = user.Id, User = null!, Name = "Unconditional rejection", TokenHash = ApiTokenService.HashToken(user.Id) });
        await db.SaveChangesAsync(); await db.Entry(trip).ReloadAsync();
        var warmup = new Mock<ICacheWarmupScheduler>();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, warmup.Object);
        });
        using var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", user.Id);
        object intent = operation switch { "notes" => new { notes = "Phone notes" }, "coordinates" => new { latitude = 15, longitude = 23 },
            "move" => (object)new { regionId = Guid.NewGuid() }, _ => new { displayOrder = 1 } };
        using var response = await client.PutAsJsonAsync($"/api/trips/places/{place.Id}", intent);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("mutation-originals-required", await response.Content.ReadAsStringAsync());
        await using var verify = fixture.CreateContext(); var stored = await verify.Places.SingleAsync(p => p.Id == place.Id);
        Assert.Equal((place.RegionId, "Original", "Notes", "Address", (int?)null, "POINT (22 14)"),
            (stored.RegionId, stored.Name, stored.Notes, stored.Address, stored.DisplayOrder, stored.Location!.AsText()));
        Assert.Equal(trip.UpdatedAt, (await verify.Trips.FindAsync(trip.Id))!.UpdatedAt); Assert.Empty(warmup.Invocations);
    }
}
