using System.Text.Json;
using Moq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;
using Wayfarer.Parsers;
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
    private InvitationsController CreateController(ApplicationDbContext db, string userId, SseService? sse = null)
    {
        var controller = new InvitationsController(
            db,
            new InvitationService(db),
            new NullLogger<InvitationsController>(),
            sse ?? new SseService());
        return ConfigureControllerWithUser(controller, userId);
    }

    /// <summary>Duplicate pending invitations remain actionable without trusting every InvalidOperationException.</summary>
    [Theory]
    [InlineData("A pending invitation already exists for this user in the specified group", true)]
    [InlineData("private-invitation-error-663", false)]
    public async Task Create_OnlyPublishesKnownBusinessOutcome(string failure, bool known)
    {
        using var db = CreateDbContext();
        var service = new Mock<IInvitationService>();
        service.Setup(s => s.InviteUserAsync(It.IsAny<Guid>(), "u1", "u2", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(failure));
        var controller = new InvitationsController(db, service.Object, NullLogger<InvitationsController>.Instance, new SseService());
        ConfigureControllerWithUser(controller, "u1");
        var result = Assert.IsType<BadRequestObjectResult>(await controller.Create(
            new InvitationCreateRequest { GroupId = Guid.NewGuid(), InviteeUserId = "u2" }, default));
        Assert.Equal(known ? failure : "The operation could not be completed. Please try again.",
            result.Value?.GetType().GetProperty("message")?.GetValue(result.Value));
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
        await isvc.InviteUserAsync(g.Id, owner.Id, user.Id, null);

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
        var inv = await isvc.InviteUserAsync(g.Id, owner.Id, user.Id, null);

        var sse = new RecordingSseService();
        var ctrl = CreateController(db, user.Id, sse);

        // Act
        var resp = await ctrl.Accept(inv.Id, CancellationToken.None);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(resp);
        Assert.True(await db.GroupMembers.AnyAsync(m => m.GroupId == g.Id && m.UserId == user.Id));
        Assert.Contains(($"group-notifications-{user.Id}", SseService.InvitationStateHint), sse.Messages);
        Assert.Contains(($"group-notifications-{user.Id}", SseService.MembershipStateHint), sse.Messages);
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
    public async Task Create_ReturnsBadRequest_WhenRecipientMissing()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var ctrl = CreateController(db, user.Id);

        var resp = await ctrl.Create(new InvitationCreateRequest
        {
            GroupId = Guid.NewGuid(),
            InviteeUserId = null
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
        var sse = new RecordingSseService();
        var ctrl = CreateController(db, owner.Id, sse);

        var resp = await ctrl.Create(new InvitationCreateRequest
        {
            GroupId = group.Id,
            InviteeUserId = invitee.Id
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(resp);
        Assert.True(await db.GroupInvitations.AnyAsync(i => i.GroupId == group.Id && i.InviteeUserId == invitee.Id));
        Assert.Contains(($"group-notifications-{invitee.Id}", SseService.InvitationStateHint), sse.Messages);
    }

    [Fact]
    public async Task Create_RejectsFormerEmailOnlyPayload()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var gs = new GroupService(db);
        var group = await gs.CreateGroupAsync(owner.Id, "TestGroup", null);
        var ctrl = CreateController(db, owner.Id);

        // Deserialize the old wire shape through the current request contract.
        var request = JsonSerializer.Deserialize<InvitationCreateRequest>(
            JsonSerializer.Serialize(new { GroupId = group.Id, InviteeEmail = "test@example.com" }))!;
        var sse = new RecordingSseService();
        var resp = await CreateController(db, owner.Id, sse).Create(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(resp);
        Assert.Empty(await db.GroupInvitations.ToListAsync());
        Assert.Empty(sse.Messages);
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
        var inv = await isvc.InviteUserAsync(group.Id, owner.Id, user.Id, null);

        var sse = new RecordingSseService();
        var ctrl = CreateController(db, user.Id, sse);

        var resp = await ctrl.Decline(inv.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(resp);
        var declined = await db.GroupInvitations.FirstAsync(i => i.Id == inv.Id);
        Assert.Equal(GroupInvitation.InvitationStatuses.Declined, declined.Status);
        Assert.Contains(($"group-notifications-{user.Id}", SseService.InvitationStateHint), sse.Messages);
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

    /// <summary>Lists only the caller's explicit invitations, excluding unresolved legacy history.</summary>
    [Fact]
    public async Task List_RestrictsRecipientsAndOmitsObsoleteEmailProjection()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var recipient = TestDataFixtures.CreateUser(id: "recipient");
        var other = TestDataFixtures.CreateUser(id: "other");
        db.Users.AddRange(owner, recipient, other);
        var group = await new GroupService(db).CreateGroupAsync(owner.Id, "Recipients", null);
        var addressed = TestDataFixtures.CreateGroupInvitation(group, owner, recipient);
        db.GroupInvitations.AddRange(addressed,
            TestDataFixtures.CreateGroupInvitation(group, owner),
            TestDataFixtures.CreateGroupInvitation(group, owner, other));
        await db.SaveChangesAsync();

        var response = Assert.IsType<OkObjectResult>(await CreateController(db, recipient.Id)
            .ListForCurrentUser(CancellationToken.None));

        var item = Assert.Single(JsonSerializer.SerializeToElement(response.Value).EnumerateArray());
        Assert.Equal(addressed.Id, item.GetProperty("Id").GetGuid());
        Assert.False(item.TryGetProperty("InviteeEmail", out _));
    }

    /// <summary>ID endpoints fail closed for unresolved or wrong recipients and publish nothing.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WrongRecipientCannotRespondThroughIdEndpoint(bool identified, bool decline)
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var caller = TestDataFixtures.CreateUser(id: "caller");
        db.Users.AddRange(owner, caller);
        var group = await new GroupService(db).CreateGroupAsync(owner.Id, "Recipients", null);
        var invitation = TestDataFixtures.CreateGroupInvitation(group, owner, identified ? owner : null);
        db.GroupInvitations.Add(invitation);
        await db.SaveChangesAsync();
        var sse = new RecordingSseService();
        var controller = CreateController(db, caller.Id, sse);

        var result = decline ? await controller.Decline(invitation.Id, default)
            : await controller.Accept(invitation.Id, default);

        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Empty(sse.Messages);
        db.ChangeTracker.Clear();
        Assert.Equal(GroupInvitation.InvitationStatuses.Pending, (await db.GroupInvitations.SingleAsync()).Status);
        Assert.False(await db.GroupMembers.AnyAsync(m => m.UserId == caller.Id));
    }

    private sealed class RecordingSseService : SseService
    {
        public List<(string Channel, string Data)> Messages { get; } = [];
        public override Task BroadcastAsync(string channel, string data)
        {
            Messages.Add((channel, data));
            return Task.CompletedTask;
        }
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
