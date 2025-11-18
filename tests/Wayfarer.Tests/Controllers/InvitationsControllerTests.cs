using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Tests for the InvitationsController API endpoints.
/// </summary>
public class InvitationsControllerTests : TestBase
{
    /// <summary>
    /// Creates an InvitationsController configured with the specified user for authentication.
    /// </summary>
    /// <param name="db">The database context.</param>
    /// <param name="userId">The authenticated user ID.</param>
    /// <returns>A configured InvitationsController instance.</returns>
    private InvitationsController CreateController(ApplicationDbContext db, string userId)
    {
        var controller = new InvitationsController(
            db,
            new InvitationService(db),
            new NullLogger<InvitationsController>());
        return ConfigureControllerWithUser(controller, userId);
    }

    [Fact]
    public async Task List_Returns_Pending()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "o");
        var user = TestDataFixtures.CreateUser(id: "u");
        db.Users.AddRange(owner, user);
        await db.SaveChangesAsync();

        var gs = new GroupService(db);
        var isvc = new InvitationService(db);
        var g = await gs.CreateGroupAsync(owner.Id, "G", null);
        await isvc.InviteUserAsync(g.Id, owner.Id, user.Id, null, null);

        var ctrl = CreateController(db, user.Id);

        // Act
        var resp = await ctrl.ListForCurrentUser(CancellationToken.None);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(resp);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public async Task Accept_By_Id_Works()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "o");
        var user = TestDataFixtures.CreateUser(id: "u");
        db.Users.AddRange(owner, user);
        await db.SaveChangesAsync();

        var gs = new GroupService(db);
        var isvc = new InvitationService(db);
        var g = await gs.CreateGroupAsync(owner.Id, "G2", null);
        var inv = await isvc.InviteUserAsync(g.Id, owner.Id, user.Id, null, null);

        var ctrl = CreateController(db, user.Id);

        // Act
        var resp = await ctrl.Accept(inv.Id, CancellationToken.None);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(resp);
        Assert.True(await db.GroupMembers.AnyAsync(m => m.GroupId == g.Id && m.UserId == user.Id));
    }
}
