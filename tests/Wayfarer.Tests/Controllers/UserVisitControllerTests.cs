using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Tests for the User area VisitController.
/// </summary>
public class UserVisitControllerTests : TestBase
{
    [Fact]
    public void Index_ReturnsView()
    {
        var db = CreateDbContext();
        var controller = BuildController(db, "u1");

        var result = controller.Index();

        Assert.IsType<ViewResult>(result);
    }

    [Fact]
    public async Task Edit_Get_ReturnsView_WhenOwned()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1"));
        var visit = CreateVisit("u1", "My Place");
        db.PlaceVisitEvents.Add(visit);
        db.SaveChanges();
        var controller = BuildController(db, "u1");

        var result = await controller.Edit(visit.Id, "/User/Visit");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<VisitEditViewModel>(view.Model);
        Assert.Equal(visit.Id, model.Id);
        Assert.Equal("My Place", model.PlaceNameSnapshot);
    }

    [Fact]
    public async Task Edit_Post_UpdatesVisit_WhenValid()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1"));
        var visit = CreateVisit("u1", "Original Place");
        db.PlaceVisitEvents.Add(visit);
        db.SaveChanges();
        var controller = BuildController(db, "u1");

        var vm = new VisitEditViewModel
        {
            Id = visit.Id,
            ArrivedAtUtc = DateTime.UtcNow.AddHours(-2),
            EndedAtUtc = DateTime.UtcNow.AddHours(-1),
            Latitude = 40.0,
            Longitude = -74.0,
            IconNameSnapshot = "restaurant",
            MarkerColorSnapshot = "bg-red",
            NotesHtml = "<p>Updated notes</p>",
            ReturnUrl = "/User/Visit"
        };

        var result = await controller.Edit(vm, "return");

        Assert.IsType<RedirectResult>(result);
        var updated = db.PlaceVisitEvents.Find(visit.Id);
        Assert.NotNull(updated);
        Assert.Equal("restaurant", updated.IconNameSnapshot);
        Assert.Equal("<p>Updated notes</p>", updated.NotesHtml);
    }

    [Fact]
    public async Task Edit_Post_StaysOnPage_WhenSaveOnly()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1"));
        var visit = CreateVisit("u1", "My Place");
        db.PlaceVisitEvents.Add(visit);
        db.SaveChanges();
        var controller = BuildController(db, "u1");

        var vm = new VisitEditViewModel
        {
            Id = visit.Id,
            ArrivedAtUtc = DateTime.UtcNow.AddHours(-2),
            Latitude = 40.0,
            Longitude = -74.0,
            ReturnUrl = "/User/Visit"
        };

        var result = await controller.Edit(vm, "save");

        Assert.IsType<RedirectToActionResult>(result);
    }

    [Fact]
    public async Task Delete_RemovesVisit_WhenOwned()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1"));
        var visit = CreateVisit("u1", "To Delete");
        db.PlaceVisitEvents.Add(visit);
        db.SaveChanges();
        var visitId = visit.Id;
        var controller = BuildController(db, "u1");

        var result = await controller.Delete(visitId, "/User/Visit");

        Assert.IsType<RedirectResult>(result);
        Assert.Null(db.PlaceVisitEvents.Find(visitId));
    }

    private VisitController BuildController(ApplicationDbContext db, string userId)
    {
        var controller = new VisitController(
            NullLogger<BaseController>.Instance,
            db);

        var mockUrlHelper = new Mock<IUrlHelper>();
        mockUrlHelper.Setup(x => x.IsLocalUrl(It.IsAny<string>())).Returns(true);
        mockUrlHelper.Setup(x => x.Action(It.IsAny<UrlActionContext>())).Returns("/User/Visit");

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = CreateUserPrincipal(userId) }
        };
        controller.Url = mockUrlHelper.Object;
        controller.TempData = new TempDataDictionary(
            controller.ControllerContext.HttpContext,
            new Mock<ITempDataProvider>().Object);

        return controller;
    }

    private static PlaceVisitEvent CreateVisit(
        string userId,
        string placeName,
        Guid? tripId = null,
        string? regionName = null)
    {
        return new PlaceVisitEvent
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PlaceId = Guid.NewGuid(),
            PlaceNameSnapshot = placeName,
            TripIdSnapshot = tripId ?? Guid.NewGuid(),
            TripNameSnapshot = "Test Trip",
            RegionNameSnapshot = regionName ?? "Test Region",
            PlaceLocationSnapshot = new Point(-74.0, 40.0) { SRID = 4326 },
            ArrivedAtUtc = DateTime.UtcNow.AddHours(-1),
            LastSeenAtUtc = DateTime.UtcNow,
            IconNameSnapshot = "marker",
            MarkerColorSnapshot = "bg-blue"
        };
    }
}
