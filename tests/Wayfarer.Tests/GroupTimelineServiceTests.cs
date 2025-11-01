using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Xunit;
using LocationEntity = Wayfarer.Models.Location;

namespace Wayfarer.Tests;

public class GroupTimelineServiceTests
{
    private static (ApplicationDbContext Db, GroupTimelineService Service) CreateService(string? groupType, Action<ApplicationDbContext>? seedMembers)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new ApplicationDbContext(options, new ServiceCollection().BuildServiceProvider());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MobileGroups:Query:DefaultPageSize"] = "3",
                ["MobileGroups:Query:MaxPageSize"] = "5"
            })
            .Build();

        var caller = new ApplicationUser { Id = "caller", UserName = "caller", DisplayName = "Caller", IsActive = true };
        db.Users.Add(caller);
        db.Groups.Add(new Group
        {
            Id = Guid.NewGuid(),
            Name = "Test",
            GroupType = groupType,
            OwnerUserId = caller.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        db.ApplicationSettings.Add(new ApplicationSettings { LocationTimeThresholdMinutes = 5, LocationDistanceThresholdMeters = 15 });
        db.SaveChanges();

        seedMembers?.Invoke(db);
        db.SaveChanges();

        var service = new GroupTimelineService(db, new LocationService(db), configuration);
        return (db, service);
    }

    [Fact]
    public async Task BuildAccessContext_FriendsHonoursOptOut()
    {
        static void Seed(ApplicationDbContext db)
        {
            db.Users.AddRange(
                new ApplicationUser { Id = "friend-allowed", UserName = "friendA", DisplayName = "Friend A", IsActive = true },
                new ApplicationUser { Id = "friend-optout", UserName = "friendB", DisplayName = "Friend B", IsActive = true });

            db.GroupMembers.AddRange(
                new GroupMember { GroupId = db.Groups.Single().Id, UserId = "caller", Role = GroupMember.Roles.Owner, Status = GroupMember.MembershipStatuses.Active, JoinedAt = DateTime.UtcNow },
                new GroupMember { GroupId = db.Groups.Single().Id, UserId = "friend-allowed", Role = GroupMember.Roles.Member, Status = GroupMember.MembershipStatuses.Active, JoinedAt = DateTime.UtcNow },
                new GroupMember { GroupId = db.Groups.Single().Id, UserId = "friend-optout", Role = GroupMember.Roles.Member, Status = GroupMember.MembershipStatuses.Active, OrgPeerVisibilityAccessDisabled = true, JoinedAt = DateTime.UtcNow });
        }

        var (db, service) = CreateService("Friends", Seed);
        var context = await service.BuildAccessContextAsync(db.Groups.Single().Id, "caller");

        Assert.NotNull(context);
        Assert.True(context!.AllowedUserIds.Contains("friend-allowed"));
        Assert.False(context.AllowedUserIds.Contains("friend-optout"));
    }

    [Fact]
    public async Task GetLatestLocationsAsync_FriendsSkipOptedOutMembers()
    {
        static void Seed(ApplicationDbContext db)
        {
            var groupId = db.Groups.Single().Id;
            db.Users.AddRange(
                new ApplicationUser { Id = "friend-allowed", UserName = "friendA", DisplayName = "Friend A", IsActive = true },
                new ApplicationUser { Id = "friend-optout", UserName = "friendB", DisplayName = "Friend B", IsActive = true });

            db.GroupMembers.AddRange(
                new GroupMember { GroupId = groupId, UserId = "caller", Role = GroupMember.Roles.Owner, Status = GroupMember.MembershipStatuses.Active, JoinedAt = DateTime.UtcNow },
                new GroupMember { GroupId = groupId, UserId = "friend-allowed", Role = GroupMember.Roles.Member, Status = GroupMember.MembershipStatuses.Active, JoinedAt = DateTime.UtcNow },
                new GroupMember { GroupId = groupId, UserId = "friend-optout", Role = GroupMember.Roles.Member, Status = GroupMember.MembershipStatuses.Active, OrgPeerVisibilityAccessDisabled = true, JoinedAt = DateTime.UtcNow });

            db.Locations.AddRange(
                new LocationEntity { UserId = "friend-allowed", Coordinates = new NetTopologySuite.Geometries.Point(10, 10) { SRID = 4326 }, Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow, TimeZoneId = "UTC" },
                new LocationEntity { UserId = "friend-optout", Coordinates = new NetTopologySuite.Geometries.Point(11, 11) { SRID = 4326 }, Timestamp = DateTime.UtcNow.AddMinutes(-1), LocalTimestamp = DateTime.UtcNow.AddMinutes(-1), TimeZoneId = "UTC" });
        }

        var (db, service) = CreateService("Friends", Seed);
        var context = await service.BuildAccessContextAsync(db.Groups.Single().Id, "caller");
        var results = await service.GetLatestLocationsAsync(context!, new[] { "friend-allowed", "friend-optout" });

        Assert.Single(results);
        Assert.Equal("friend-allowed", results[0].UserId);
    }

    [Fact]
    public async Task BuildAccessContext_OrganizationIncludesAllMembers()
    {
        static void Seed(ApplicationDbContext db)
        {
            var groupId = db.Groups.Single().Id;
            db.Users.AddRange(
                new ApplicationUser { Id = "friend-allowed", UserName = "friendA", DisplayName = "Friend A", IsActive = true },
                new ApplicationUser { Id = "friend-optout", UserName = "friendB", DisplayName = "Friend B", IsActive = true });

            db.GroupMembers.AddRange(
                new GroupMember { GroupId = groupId, UserId = "caller", Role = GroupMember.Roles.Owner, Status = GroupMember.MembershipStatuses.Active, JoinedAt = DateTime.UtcNow },
                new GroupMember { GroupId = groupId, UserId = "friend-allowed", Role = GroupMember.Roles.Member, Status = GroupMember.MembershipStatuses.Active, JoinedAt = DateTime.UtcNow },
                new GroupMember { GroupId = groupId, UserId = "friend-optout", Role = GroupMember.Roles.Member, Status = GroupMember.MembershipStatuses.Active, OrgPeerVisibilityAccessDisabled = true, JoinedAt = DateTime.UtcNow });
        }

        var (db, service) = CreateService("Organization", Seed);
        var context = await service.BuildAccessContextAsync(db.Groups.Single().Id, "caller");

        Assert.NotNull(context);
        Assert.True(context!.AllowedUserIds.Contains("friend-allowed"));
        Assert.True(context.AllowedUserIds.Contains("friend-optout"));
    }
}
