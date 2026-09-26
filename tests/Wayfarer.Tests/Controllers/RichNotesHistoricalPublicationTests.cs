using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Dtos.Editor;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public partial class ApiTripsControllerTests
{
    /// <summary>Historical aggregate publication and no-op echoes stay safe without persisting read normalization.</summary>
    [Fact]
    public async Task HistoricalTrip_PublicationAndNullMutationAreNonMutating()
    {
        const string input = "<p class=\"ql-align-justify\"><s>safe</s></p><script>bad()</script>";
        using var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip", Notes = input };
        var region = new Region { Id = Guid.NewGuid(), Trip = trip, UserId = user.Id, Name = "Region", Notes = input };
        var place = new Place { Id = Guid.NewGuid(), Region = region, UserId = user.Id, Name = "Place", Notes = input };
        trip.Regions = [region]; region.Places = [place];
        db.Trips.Add(trip); await db.SaveChangesAsync();
        var controller = BuildController(db, token: "tok");
        var read = Assert.IsType<OkObjectResult>(controller.GetTrip(trip.Id));
        var json = JsonSerializer.SerializeToElement(read.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("<p class=\"ql-align-justify\"><s>safe</s></p>", json.GetProperty("notes").GetString());
        Assert.DoesNotContain("bad()", json.ToString());
        var state = EditorTripStateMapper.ToEditorState(trip, new Dictionary<Guid, IReadOnlyList<PlaceVisitEvent>>(),
            new EditorOptionsDto([], [], [], [], new("Area", "#ffffff"), new(10, 10, ""), new(10, 2)), null, null);
        Assert.DoesNotContain("bad()", JsonSerializer.Serialize(state));
        var echo = Assert.IsType<OkObjectResult>(await controller.UpdateTrip(trip.Id, new TripUpdateRequestDto()));
        Assert.DoesNotContain("bad()", JsonSerializer.Serialize(echo.Value));
        Assert.Equal(input, trip.Notes);
        Assert.Equal(input, region.Notes);
        Assert.Equal(input, place.Notes);
        Assert.All(db.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
    }
}

public partial class ApiLocationControllerTests
{
    /// <summary>Null updates preserve old storage while entity echoes and timeline DTOs expose only safe HTML.</summary>
    [Fact]
    public async Task HistoricalLocation_NullMutationAndTimelineDoNotRewriteSource()
    {
        using var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var location = CreateLocation(user.Id, 655);
        location.Notes = "<p onclick='bad()'>safe</p><script>bad()</script>";
        db.Locations.Add(location); await db.SaveChangesAsync();
        var response = Assert.IsType<OkObjectResult>(await BuildApiController(db, user).Update(location.Id, new LocationUpdateRequestDto()));
        var copy = (Location)response.Value!.GetType().GetProperty("location")!.GetValue(response.Value)!;
        Assert.Equal("<p>safe</p>", copy.Notes);
        Assert.NotSame(location, copy);
        var stamp = location.LocalTimestamp;
        var timeline = await new Wayfarer.Parsers.LocationService(db).GetLocationsByDateAsync(
            user.Id, "day", stamp.Year, stamp.Month, stamp.Day);
        Assert.Equal("<p>safe</p>", Assert.Single(timeline.Locations).Notes);
        Assert.Contains("<script>", location.Notes);
        Assert.Equal(EntityState.Unchanged, db.Entry(location).State);
    }
}
