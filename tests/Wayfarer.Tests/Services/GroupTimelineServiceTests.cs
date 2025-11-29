using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using LocationEntity = Wayfarer.Models.Location;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests for the GroupTimelineService which handles group-based location sharing.
/// </summary>
public class GroupTimelineServiceTests : TestBase
{
    /// <summary>
    /// Creates a GroupTimelineService with test configuration.
    /// </summary>
    /// <param name="groupType">The type of group (Friends, Organization, etc.).</param>
    /// <param name="seedMembers">Optional action to seed additional members and data.</param>
    /// <returns>A tuple containing the database context and configured service.</returns>
    private (ApplicationDbContext Db, GroupTimelineService Service) CreateService(
        string? groupType,
        Action<ApplicationDbContext>? seedMembers = null)
    {
        var db = CreateDbContext();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MobileGroups:Query:DefaultPageSize"] = "3",
                ["MobileGroups:Query:MaxPageSize"] = "5"
            })
            .Build();

        var caller = TestDataFixtures.CreateUser(id: "caller");
        db.Users.Add(caller);

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Test",
            GroupType = groupType,
            OwnerUserId = caller.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Groups.Add(group);

        db.ApplicationSettings.Add(new ApplicationSettings
        {
            LocationTimeThresholdMinutes = 5,
            LocationDistanceThresholdMeters = 15
        });
        db.SaveChanges();

        seedMembers?.Invoke(db);
        db.SaveChanges();

        var service = new GroupTimelineService(db, new LocationService(db), configuration);
        return (db, service);
    }

    [Fact]
    public async Task BuildAccessContext_FriendsHonoursOptOut()
    {
        // Arrange
        static void Seed(ApplicationDbContext db)
        {
            var friendAllowed = TestDataFixtures.CreateUser(id: "friend-allowed", username: "friendA", displayName: "Friend A");
            var friendOptout = TestDataFixtures.CreateUser(id: "friend-optout", username: "friendB", displayName: "Friend B");
            db.Users.AddRange(friendAllowed, friendOptout);

            var groupId = db.Groups.Single().Id;
            db.GroupMembers.AddRange(
                new GroupMember
                {
                    GroupId = groupId, UserId = "caller", Role = GroupMember.Roles.Owner,
                    Status = GroupMember.MembershipStatuses.Active, JoinedAt = DateTime.UtcNow
                },
                new GroupMember
                {
                    GroupId = groupId, UserId = "friend-allowed", Role = GroupMember.Roles.Member,
                    Status = GroupMember.MembershipStatuses.Active, JoinedAt = DateTime.UtcNow
                },
                new GroupMember
                {
                    GroupId = groupId, UserId = "friend-optout", Role = GroupMember.Roles.Member,
                    Status = GroupMember.MembershipStatuses.Active, OrgPeerVisibilityAccessDisabled = true,
                    JoinedAt = DateTime.UtcNow
                });
        }

        var (db, service) = CreateService("Friends", Seed);

        // Act
        var context = await service.BuildAccessContextAsync(db.Groups.Single().Id, "caller");

        // Assert
        Assert.NotNull(context);
        Assert.Contains("friend-allowed", context!.AllowedUserIds);
        Assert.DoesNotContain("friend-optout", context.AllowedUserIds);
    }

    [Fact]
    public async Task GetLatestLocationsAsync_FriendsSkipOptedOutMembers()
    {
        // Arrange
        static void Seed(ApplicationDbContext db)
        {
            var friendAllowed = TestDataFixtures.CreateUser(id: "friend-allowed", username: "friendA", displayName: "Friend A");
            var friendOptout = TestDataFixtures.CreateUser(id: "friend-optout", username: "friendB", displayName: "Friend B");
            db.Users.AddRange(friendAllowed, friendOptout);

            var groupId = db.Groups.Single().Id;
            db.GroupMembers.AddRange(
                new GroupMember
                {
                    GroupId = groupId, UserId = "caller", Role = GroupMember.Roles.Owner,
                    Status = GroupMember.MembershipStatuses.Active, JoinedAt = DateTime.UtcNow
                },
                new GroupMember
                {
                    GroupId = groupId, UserId = "friend-allowed", Role = GroupMember.Roles.Member,
                    Status = GroupMember.MembershipStatuses.Active, JoinedAt = DateTime.UtcNow
                },
                new GroupMember
                {
                    GroupId = groupId, UserId = "friend-optout", Role = GroupMember.Roles.Member,
                    Status = GroupMember.MembershipStatuses.Active, OrgPeerVisibilityAccessDisabled = true,
                    JoinedAt = DateTime.UtcNow
                });

            db.Locations.AddRange(
                new LocationEntity
                {
                    UserId = "friend-allowed",
                    Coordinates = new NetTopologySuite.Geometries.Point(10, 10) { SRID = 4326 },
                    Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow, TimeZoneId = "UTC"
                },
                new LocationEntity
                {
                    UserId = "friend-optout",
                    Coordinates = new NetTopologySuite.Geometries.Point(11, 11) { SRID = 4326 },
                    Timestamp = DateTime.UtcNow.AddMinutes(-1), LocalTimestamp = DateTime.UtcNow.AddMinutes(-1),
                    TimeZoneId = "UTC"
                });
        }

        var (db, service) = CreateService("Friends", Seed);
        var context = await service.BuildAccessContextAsync(db.Groups.Single().Id, "caller");

        // Act
        var results = await service.GetLatestLocationsAsync(context!, new[] { "friend-allowed", "friend-optout" });

        // Assert
        Assert.Single(results);
        Assert.Equal("friend-allowed", results[0].UserId);
    }

    [Fact]
    public async Task BuildAccessContext_OrganizationIncludesAllMembers()
    {
        // Arrange
        static void Seed(ApplicationDbContext db)
        {
            var friendAllowed = TestDataFixtures.CreateUser(id: "friend-allowed", username: "friendA", displayName: "Friend A");
            var friendOptout = TestDataFixtures.CreateUser(id: "friend-optout", username: "friendB", displayName: "Friend B");
            db.Users.AddRange(friendAllowed, friendOptout);

            var groupId = db.Groups.Single().Id;
            db.GroupMembers.AddRange(
                new GroupMember
                {
                    GroupId = groupId, UserId = "caller", Role = GroupMember.Roles.Owner,
                    Status = GroupMember.MembershipStatuses.Active, JoinedAt = DateTime.UtcNow
                },
                new GroupMember
                {
                    GroupId = groupId, UserId = "friend-allowed", Role = GroupMember.Roles.Member,
                    Status = GroupMember.MembershipStatuses.Active, JoinedAt = DateTime.UtcNow
                },
                new GroupMember
                {
                    GroupId = groupId, UserId = "friend-optout", Role = GroupMember.Roles.Member,
                    Status = GroupMember.MembershipStatuses.Active, OrgPeerVisibilityAccessDisabled = true,
                    JoinedAt = DateTime.UtcNow
                });
        }

        var (db, service) = CreateService("Organization", Seed);

        // Act
        var context = await service.BuildAccessContextAsync(db.Groups.Single().Id, "caller");

        // Assert
        Assert.NotNull(context);
        Assert.Contains("friend-allowed", context!.AllowedUserIds);
        Assert.Contains("friend-optout", context.AllowedUserIds);
    }
}
