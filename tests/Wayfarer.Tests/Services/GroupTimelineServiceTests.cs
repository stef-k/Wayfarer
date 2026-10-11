using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
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

    /// <summary>
    /// Enforces the production Friends audience before subject counting, retaining the caller's full month.
    /// </summary>
    /// <param name="userIds">Explicit service subjects; adapters own any normalization or defaults.</param>
    /// <param name="expectedCount">Two caller markers when selected exactly, otherwise no authorized results.</param>
    [Theory]
    [InlineData(new[] { "hidden", "caller", "outside" }, 2)]
    [InlineData(new[] { "caller", "hidden" }, 2)]
    [InlineData(new[] { "outside", "caller" }, 2)]
    [InlineData(new[] { "outside" }, 0)]
    [InlineData(new[] { "hidden" }, 0)]
    [InlineData(new[] { "hidden", "outside" }, 0)]
    [InlineData(new string[0], 0)]
    [InlineData(new[] { "CALLER", " caller " }, 0)]
    public async Task SelectHistoryLocationsAsync_FriendsEnforcesAuthorizedSelectionBeforeDateRestriction(
        string[] userIds, int expectedCount)
    {
        var (db, service) = CreateService("Friends");
        var group = db.Groups.Single();
        var caller = db.Users.Single();
        var hidden = TestDataFixtures.CreateUser(id: "hidden");
        var outside = TestDataFixtures.CreateUser(id: "outside");
        db.Users.AddRange(hidden, outside);
        var hiddenMember = TestDataFixtures.CreateGroupMember(group, hidden);
        hiddenMember.OrgPeerVisibilityAccessDisabled = true;
        db.GroupMembers.AddRange(
            TestDataFixtures.CreateGroupMember(group, caller, GroupMember.Roles.Owner), hiddenMember);

        var captureDay = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var callerHistory = new[]
        {
            TestDataFixtures.CreateLocation(caller, latitude: 10, longitude: 10, timestamp: captureDay),
            TestDataFixtures.CreateLocation(caller, latitude: 10, longitude: 10, timestamp: captureDay.AddDays(1))
        };
        db.Locations.AddRange(callerHistory);
        db.Locations.AddRange(
            TestDataFixtures.CreateLocation(hidden, latitude: 10, longitude: 10, timestamp: captureDay),
            TestDataFixtures.CreateLocation(outside, latitude: 10, longitude: 10, timestamp: captureDay));
        await db.SaveChangesAsync();

        // Build actual persisted access: the opted-out peer is active, and the outside user has no membership.
        var context = await service.BuildAccessContextAsync(group.Id, caller.Id);
        Assert.NotNull(context);
        Assert.True(context.IsMember);
        Assert.Equal(new[] { caller.Id }, context.AllowedUserIds);
        var request = new GroupLocationsQueryRequest
        {
            DateType = "month", Year = 2026, Month = 1, Day = 15,
            MinLng = 9, MinLat = 9, MaxLng = 11, MaxLat = 11
        };

        var selection = await service.SelectHistoryLocationsAsync(context, userIds, request);

        Assert.Equal(expectedCount, selection.TotalItems);
        Assert.Equal(callerHistory.Take(expectedCount).Select(location => location.Id),
            selection.Results.Select(location => location.Id));
        Assert.All(selection.Results, location => Assert.Equal(caller.Id, location.UserId));
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
                    Notes = "<p>safe</p><script>bad()</script>",
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
        Assert.Equal("<p>safe</p>", results[0].Notes);
        Assert.Contains("<script>", db.Locations.Single(l => l.UserId == "friend-allowed").Notes);
    }

    [Fact]
    public async Task BuildAccessContext_OrganizationDefaultsToSelfOnly()
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
        Assert.Equal(new[] { "caller" }, context!.AllowedUserIds);
    }
}
