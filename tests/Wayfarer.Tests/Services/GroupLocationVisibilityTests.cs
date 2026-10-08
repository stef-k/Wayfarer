using Wayfarer.Models;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Owns the audience matrix once, independently of transport and persistence.</summary>
public sealed class GroupLocationVisibilityTests
{
    /// <summary>Source opt-out combinations apply equally to each active recipient Group role.</summary>
    public static IEnumerable<object[]> Audiences() =>
        from policy in new[]
        {
            (Type: "Family", Enabled: false, Hidden: false, Visible: true),
            (Type: "Family", Enabled: false, Hidden: true, Visible: true),
            (Type: "Friends", Enabled: false, Hidden: false, Visible: true),
            (Type: "Friends", Enabled: false, Hidden: true, Visible: false),
            (Type: "Organization", Enabled: false, Hidden: false, Visible: false),
            (Type: "Organization", Enabled: false, Hidden: true, Visible: false),
            (Type: "Organization", Enabled: true, Hidden: false, Visible: true),
            (Type: "Organization", Enabled: true, Hidden: true, Visible: false),
        }
        from role in new[] { GroupMember.Roles.Owner, GroupMember.Roles.Manager, GroupMember.Roles.Member }
        select new object[] { policy.Type, policy.Enabled, policy.Hidden, policy.Visible, role };

    /// <summary>Own opt-out never removes access to willing peers or self, and management never bypasses policy.</summary>
    [Theory]
    [MemberData(nameof(Audiences))]
    public void AudienceRespectsSourcePolicy(string type, bool enabled, bool hidden, bool visible, string role)
    {
        var group = new Group { Id = Guid.NewGuid(), Name = "Audience", OwnerUserId = "recipient", GroupType = type, OrgPeerVisibilityEnabled = enabled };
        var recipient = new GroupMember { GroupId = group.Id, UserId = "recipient", Role = role, OrgPeerVisibilityAccessDisabled = true };
        var source = new GroupMember { GroupId = group.Id, UserId = "source", OrgPeerVisibilityAccessDisabled = hidden };
        Assert.Equal(visible, GroupLocationVisibility.CanSee(group, recipient, source));
        Assert.True(GroupLocationVisibility.CanSee(group, recipient, recipient));
        Assert.Equal(role != GroupMember.Roles.Member, GroupLocationVisibility.CanManage(recipient));
    }

    /// <summary>Neither ownership metadata nor a source/recipient's previous membership permits location data.</summary>
    [Theory]
    [InlineData("Left", false)]
    [InlineData("Removed", false)]
    [InlineData("Left", true)]
    [InlineData("Removed", true)]
    public void EndedMembershipCannotSeeOrSupplyLocations(string status, bool sourceEnded)
    {
        var group = new Group { Id = Guid.NewGuid(), Name = "Family", OwnerUserId = "owner", GroupType = "Family" };
        var owner = new GroupMember { GroupId = group.Id, UserId = "owner", Role = GroupMember.Roles.Owner };
        var source = new GroupMember { GroupId = group.Id, UserId = "source" };
        (sourceEnded ? source : owner).Status = status;
        Assert.False(GroupLocationVisibility.CanSee(group, owner, source));
        if (!sourceEnded) Assert.False(GroupLocationVisibility.CanManage(owner));
    }

    /// <summary>Archived groups and mismatched membership projections fail closed, including self.</summary>
    [Fact]
    public void ArchiveAndForeignMembershipFailClosed()
    {
        var group = new Group { Id = Guid.NewGuid(), Name = "Family", OwnerUserId = "owner", GroupType = "Family", IsArchived = true };
        var member = new GroupMember { GroupId = group.Id, UserId = "owner" };
        Assert.False(GroupLocationVisibility.CanSee(group, member, member));
        group.IsArchived = false;
        member.GroupId = Guid.NewGuid();
        Assert.False(GroupLocationVisibility.CanSee(group, member, member));
        Assert.False(GroupLocationVisibility.CanSee(group, null, member));
    }
}
