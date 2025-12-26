using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Integration;

/// <summary>
/// Tests for group membership SSE event broadcasts.
/// </summary>
public class GroupMembershipSseBroadcastsTests
{
    private sealed class TestSseService : SseService
    {
        public List<(string Channel, string Data)> Messages { get; } = new();

        public override Task BroadcastAsync(string channel, string data)
        {
            Messages.Add((channel, data));
            return Task.CompletedTask;
        }
    }

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new ApplicationDbContext(options, new ServiceCollection().BuildServiceProvider());
    }

    private static ClaimsPrincipal CreateUser(string userId)
    {
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId) };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    [Trait("Category", "GroupMembershipSseBroadcasts")]
    public async Task SetPeerVisibility_BroadcastsVisibilityChangedEvent()
    {
        using var db = CreateDb();
        var sse = new TestSseService();

        var userId = "user-visibility";
        var user = new ApplicationUser
        {
            Id = userId,
            UserName = "visibilityuser",
            DisplayName = "Visibility User",
            Email = "vis@example.com",
            IsActive = true
        };
        db.Users.Add(user);

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Test Group",
            GroupType = "Friends",
            OwnerUserId = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Groups.Add(group);

        var member = new GroupMember
        {
            GroupId = group.Id,
            UserId = userId,
            Status = GroupMember.MembershipStatuses.Active,
            Role = GroupMember.Roles.Owner,
            JoinedAt = DateTime.UtcNow,
            OrgPeerVisibilityAccessDisabled = false
        };
        db.GroupMembers.Add(member);
        await db.SaveChangesAsync();

        var mockGroupService = new Mock<IGroupService>();
        var mockLocationService = new Mock<LocationService>(db);

        var controller = new GroupsController(db, mockGroupService.Object, NullLogger<GroupsController>.Instance, mockLocationService.Object, sse)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreateUser(userId) }
            }
        };

        var request = new OrgPeerVisibilityAccessRequest { Disabled = true };
        var result = await controller.SetMemberOrgPeerVisibilityAccess(group.Id, userId, request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);

        var groupMessage = Assert.Single(sse.Messages, m => m.Channel == $"group-{group.Id}");
        using var doc = JsonDocument.Parse(groupMessage.Data);
        var root = doc.RootElement;

        Assert.Equal("visibility-changed", root.GetProperty("type").GetString());
        Assert.Equal(userId, root.GetProperty("userId").GetString());
        Assert.True(root.GetProperty("disabled").GetBoolean());
    }

    [Fact]
    [Trait("Category", "GroupMembershipSseBroadcasts")]
    public async Task LeaveGroup_BroadcastsMemberLeftEvent()
    {
        using var db = CreateDb();
        var sse = new TestSseService();

        var ownerId = "owner-leave";
        var memberId = "member-leaving";

        var owner = new ApplicationUser
        {
            Id = ownerId,
            UserName = "owner",
            DisplayName = "Owner",
            Email = "owner@example.com",
            IsActive = true
        };
        var leavingMember = new ApplicationUser
        {
            Id = memberId,
            UserName = "leaving",
            DisplayName = "Leaving Member",
            Email = "leaving@example.com",
            IsActive = true
        };
        db.Users.AddRange(owner, leavingMember);

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Test Group",
            GroupType = "Friends",
            OwnerUserId = ownerId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Groups.Add(group);

        db.GroupMembers.Add(new GroupMember
        {
            GroupId = group.Id,
            UserId = ownerId,
            Status = GroupMember.MembershipStatuses.Active,
            Role = GroupMember.Roles.Owner,
            JoinedAt = DateTime.UtcNow
        });
        db.GroupMembers.Add(new GroupMember
        {
            GroupId = group.Id,
            UserId = memberId,
            Status = GroupMember.MembershipStatuses.Active,
            Role = GroupMember.Roles.Member,
            JoinedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var groupService = new GroupService(db);
        var mockLocationService = new Mock<LocationService>(db);

        var controller = new GroupsController(db, groupService, NullLogger<GroupsController>.Instance, mockLocationService.Object, sse)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreateUser(memberId) }
            }
        };

        var result = await controller.Leave(group.Id, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);

        var groupMessage = Assert.Single(sse.Messages, m => m.Channel == $"group-{group.Id}");
        using var doc = JsonDocument.Parse(groupMessage.Data);
        var root = doc.RootElement;

        Assert.Equal("member-left", root.GetProperty("type").GetString());
        Assert.Equal(memberId, root.GetProperty("userId").GetString());
    }

    [Fact]
    [Trait("Category", "GroupMembershipSseBroadcasts")]
    public async Task RemoveMember_BroadcastsMemberRemovedEvent()
    {
        using var db = CreateDb();
        var sse = new TestSseService();

        var ownerId = "owner-remove";
        var memberId = "member-removed";

        var owner = new ApplicationUser
        {
            Id = ownerId,
            UserName = "owner",
            DisplayName = "Owner",
            Email = "owner@example.com",
            IsActive = true
        };
        var removedMember = new ApplicationUser
        {
            Id = memberId,
            UserName = "removed",
            DisplayName = "Removed Member",
            Email = "removed@example.com",
            IsActive = true
        };
        db.Users.AddRange(owner, removedMember);

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Test Group",
            GroupType = "Organization",
            OwnerUserId = ownerId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Groups.Add(group);

        db.GroupMembers.Add(new GroupMember
        {
            GroupId = group.Id,
            UserId = ownerId,
            Status = GroupMember.MembershipStatuses.Active,
            Role = GroupMember.Roles.Owner,
            JoinedAt = DateTime.UtcNow
        });
        db.GroupMembers.Add(new GroupMember
        {
            GroupId = group.Id,
            UserId = memberId,
            Status = GroupMember.MembershipStatuses.Active,
            Role = GroupMember.Roles.Member,
            JoinedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var groupService = new GroupService(db);
        var mockLocationService = new Mock<LocationService>(db);

        var controller = new GroupsController(db, groupService, NullLogger<GroupsController>.Instance, mockLocationService.Object, sse)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreateUser(ownerId) }
            }
        };

        var result = await controller.RemoveMember(group.Id, memberId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);

        var groupMessage = Assert.Single(sse.Messages, m => m.Channel == $"group-{group.Id}");
        using var doc = JsonDocument.Parse(groupMessage.Data);
        var root = doc.RootElement;

        Assert.Equal("member-removed", root.GetProperty("type").GetString());
        Assert.Equal(memberId, root.GetProperty("userId").GetString());
    }
}
