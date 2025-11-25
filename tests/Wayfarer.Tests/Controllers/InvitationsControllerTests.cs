using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
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

    [Fact]
    public async Task Create_ReturnsBadRequest_WhenGroupIdEmpty()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var ctrl = CreateController(db, user.Id);

        var resp = await ctrl.Create(new InvitationCreateRequest
        {
            GroupId = Guid.Empty,
            InviteeUserId = "invitee"
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(resp);
    }

    [Fact]
    public async Task Create_ReturnsBadRequest_WhenBothInviteeFieldsEmpty()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var ctrl = CreateController(db, user.Id);

        var resp = await ctrl.Create(new InvitationCreateRequest
        {
            GroupId = Guid.NewGuid(),
            InviteeUserId = null,
            InviteeEmail = null
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(resp);
    }

    [Fact]
    public async Task Create_CreatesInvitation_WhenValidUserId()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var invitee = TestDataFixtures.CreateUser(id: "invitee");
        db.Users.AddRange(owner, invitee);
        await db.SaveChangesAsync();

        var gs = new GroupService(db);
        var group = await gs.CreateGroupAsync(owner.Id, "TestGroup", null);
        var ctrl = CreateController(db, owner.Id);

        var resp = await ctrl.Create(new InvitationCreateRequest
        {
            GroupId = group.Id,
            InviteeUserId = invitee.Id
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(resp);
        Assert.True(await db.GroupInvitations.AnyAsync(i => i.GroupId == group.Id && i.InviteeUserId == invitee.Id));
    }

    [Fact]
    public async Task Create_CreatesInvitation_WhenValidEmail()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var gs = new GroupService(db);
        var group = await gs.CreateGroupAsync(owner.Id, "TestGroup", null);
        var ctrl = CreateController(db, owner.Id);

        var resp = await ctrl.Create(new InvitationCreateRequest
        {
            GroupId = group.Id,
            InviteeEmail = "test@example.com"
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(resp);
        Assert.True(await db.GroupInvitations.AnyAsync(i => i.GroupId == group.Id && i.InviteeEmail == "test@example.com"));
    }

    [Fact]
    public async Task Create_Returns403_WhenUserNotAuthorized()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var unauthorized = TestDataFixtures.CreateUser(id: "unauthorized");
        var invitee = TestDataFixtures.CreateUser(id: "invitee");
        db.Users.AddRange(owner, unauthorized, invitee);
        await db.SaveChangesAsync();

        var gs = new GroupService(db);
        var group = await gs.CreateGroupAsync(owner.Id, "TestGroup", null);
        var ctrl = CreateController(db, unauthorized.Id);

        var resp = await ctrl.Create(new InvitationCreateRequest
        {
            GroupId = group.Id,
            InviteeUserId = invitee.Id
        }, CancellationToken.None);

        var status = Assert.IsType<StatusCodeResult>(resp);
        Assert.Equal(403, status.StatusCode);
    }

    [Fact]
    public async Task Accept_ReturnsNotFound_WhenInvitationDoesNotExist()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var ctrl = CreateController(db, user.Id);

        var resp = await ctrl.Accept(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(resp);
    }

    [Fact]
    public async Task Decline_Works()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var user = TestDataFixtures.CreateUser(id: "user");
        db.Users.AddRange(owner, user);
        await db.SaveChangesAsync();

        var gs = new GroupService(db);
        var isvc = new InvitationService(db);
        var group = await gs.CreateGroupAsync(owner.Id, "TestGroup", null);
        var inv = await isvc.InviteUserAsync(group.Id, owner.Id, user.Id, null, null);

        var ctrl = CreateController(db, user.Id);

        var resp = await ctrl.Decline(inv.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(resp);
        var declined = await db.GroupInvitations.FirstAsync(i => i.Id == inv.Id);
        Assert.Equal(GroupInvitation.InvitationStatuses.Declined, declined.Status);
    }

    [Fact]
    public async Task Decline_ReturnsNotFound_WhenInvitationDoesNotExist()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var ctrl = CreateController(db, user.Id);

        var resp = await ctrl.Decline(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(resp);
    }

    [Fact]
    public async Task List_ReturnsEmpty_WhenNoPendingInvitations()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var ctrl = CreateController(db, user.Id);

        var resp = await ctrl.ListForCurrentUser(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(resp);
        Assert.NotNull(ok.Value);
    }
}
