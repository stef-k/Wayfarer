using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>The ordinary editor's return URL is a validated owned-workspace context, never an arbitrary redirect.</summary>
public sealed class TripCompositionEditorTests : TestBase
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task EditorReturnsOnlyToActiveSameDestinationComparison(bool finished, bool otherDestination, bool allowed)
    {
        await using var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "owner");
        var trip = TestDataFixtures.CreateTrip(user, "Current C");
        var other = TestDataFixtures.CreateTrip(user, "Other C");
        db.AddRange(user, trip, other);
        var composition = new TripComposition
        {
            Id = Guid.NewGuid(), TripId = otherDestination ? other.Id : trip.Id, UserId = user.Id,
            FinishedAt = finished ? DateTime.UtcNow : null,
            SourceAId = finished ? null : Guid.NewGuid(), SourceBId = finished ? null : Guid.NewGuid(),
            SnapshotA = finished ? null : "{}", SnapshotB = finished ? null : "{}", Mappings = finished ? null : "[]"
        };
        db.Add(composition); await db.SaveChangesAsync();
        var controller = new TripController(NullLogger<TripController>.Instance, db,
            Mock.Of<ITripMapThumbnailGenerator>(), Mock.Of<ITripTagService>(), Mock.Of<ICacheWarmupScheduler>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(ClaimTypes.Role, "User")], "test")) } }
        };
        var result = Assert.IsType<ViewResult>(await controller.Edit(trip.Id, composition.Id));
        var model = Assert.IsType<TripEditorShellViewModel>(result.Model);
        Assert.Equal(allowed ? $"/User/Trip/Compose/{composition.Id}" : "/User/Trip/Index", model.TripIndexUrl);
        Assert.Equal(allowed ? $"/User/Trip/Compose/{composition.Id}" : null, result.ViewData["CompositionReturnUrl"]);
    }
}
