using System.Collections.Generic;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Tests for <see cref="HiddenAreasController"/> protecting per-user data.
/// </summary>
public class HiddenAreasControllerTests : TestBase
{
    [Fact]
    public async Task Index_ReturnsHiddenAreasForSignedInUser()
    {
        // Arrange
        var db = CreateDbContext();
        var currentUser = TestDataFixtures.CreateUser(username: "carol");
        var otherUser = TestDataFixtures.CreateUser(username: "dan");
        db.Users.AddRange(currentUser, otherUser);

        var currentHiddenAreas = new List<HiddenArea>
        {
            CreateHiddenArea(currentUser, "Area A"),
            CreateHiddenArea(currentUser, "Area B")
        };
        var foreignHiddenArea = CreateHiddenArea(otherUser, "Area X");

        db.HiddenAreas.AddRange(currentHiddenAreas);
        db.HiddenAreas.Add(foreignHiddenArea);
        await db.SaveChangesAsync();

        var controller = CreateController(db, currentUser);

        // Act
        var result = await controller.Index();

        // Assert
        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<List<HiddenArea>>(view.Model);
        Assert.Equal(2, model.Count);
        Assert.All(model, area => Assert.Equal(currentUser.Id, area.UserId));
        Assert.DoesNotContain(model, area => area.Id == foreignHiddenArea.Id);
    }

    [Fact]
    public async Task DeleteConfirmed_RemovesHiddenAreaForOwner()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(username: "owner");
        db.Users.Add(user);
        var hiddenArea = CreateHiddenArea(user, "My Area");
        db.HiddenAreas.Add(hiddenArea);
        await db.SaveChangesAsync();

        var controller = CreateController(db, user);

        // Act
        var result = await controller.DeleteConfirmed(hiddenArea.Id);

        // Assert
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Empty(db.HiddenAreas);
    }

    [Fact]
    public async Task DeleteConfirmed_DoesNothingForDifferentUser()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(username: "owner");
        var intruder = TestDataFixtures.CreateUser(username: "intruder");
        db.Users.AddRange(owner, intruder);
        var hiddenArea = CreateHiddenArea(owner, "Protected Area");
        db.HiddenAreas.Add(hiddenArea);
        await db.SaveChangesAsync();

        var controller = CreateController(db, intruder);

        // Act
        await controller.DeleteConfirmed(hiddenArea.Id);

        // Assert
        Assert.Single(db.HiddenAreas);
    }

    private static HiddenArea CreateHiddenArea(ApplicationUser owner, string name)
    {
        var coordinates = new[]
        {
            new Coordinate(0, 0),
            new Coordinate(0, 1),
            new Coordinate(1, 1),
            new Coordinate(0, 0)
        };
        var polygon = new Polygon(new LinearRing(coordinates));
        return new HiddenArea
        {
            Name = name,
            Description = $"Description for {name}",
            Area = polygon,
            UserId = owner.Id,
            User = owner
        };
    }

    private static HiddenAreasController CreateController(ApplicationDbContext db, ApplicationUser user)
    {
        var controller = new HiddenAreasController(
            NullLogger<BaseController>.Instance,
            db);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id),
                new Claim(ClaimTypes.Name, user.UserName!)
            }, "TestAuth"))
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
        controller.TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());
        return controller;
    }
}
