using System.Reflection;
using Wayfarer.Models;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Simple value semantics for group timeline access.
/// </summary>
public class GroupTimelineAccessContextTests
{
    [Fact]
    public void Constructor_SetsFlagsAndCollections()
    {
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "g1",
            OwnerUserId = "owner",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var caller = new GroupMember { UserId = "u1" };
        var actives = new List<GroupMember> { caller, new GroupMember { UserId = "u2" } };
        var allowed = new HashSet<string> { "u1", "u3" };

        var ctx = (GroupTimelineAccessContext)Activator.CreateInstance(
            typeof(GroupTimelineAccessContext),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { group, caller, actives, allowed, true, 30 },
            culture: null)!;

        Assert.Same(group, ctx.Group);
        Assert.Same(caller, ctx.CallerMembership);
        Assert.True(ctx.IsMember);
        Assert.True(ctx.IsFriends);
        Assert.Equal(30, ctx.LocationTimeThresholdMinutes);
        Assert.Equal(2, ctx.ActiveMembers.Count);
        Assert.Contains("u3", ctx.AllowedUserIds);
    }
}
