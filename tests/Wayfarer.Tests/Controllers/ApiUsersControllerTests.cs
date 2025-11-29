using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Models.Dtos;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using AppLocation = Wayfarer.Models.Location;
using Wayfarer.Areas.Api.Controllers;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// API Users endpoints: basics, delete locations, stats.
/// </summary>
public class ApiUsersControllerTests : TestBase
{
    [Fact]
    public async Task GetBasic_ReturnsNotFound_WhenUserMissing()
    {
        var db = CreateDbContext();
        var controller = BuildController(db);

        var result = await controller.GetBasic("missing", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetBasic_ReturnsUser_WhenFound()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1", username: "alice", displayName: "Alice"));
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.GetBasic("u1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        Assert.Equal("u1", payload.GetType().GetProperty("id")?.GetValue(payload));
    }

    [Fact]
    [Trait("Category", "RequiresSpatialite")]
    public async Task DeleteAllUserLocations_Forbids_WhenCallerDifferent()
    {
        var (db, connection) = CreateSqliteDb();
        await using var _ = connection;
        await using var __ = db;
        var user = TestDataFixtures.CreateUser(id: "target");
        db.Users.Add(user);
        db.Locations.Add(new AppLocation { UserId = user.Id, Coordinates = new NetTopologySuite.Geometries.Point(0, 0) { SRID = 4326 }, Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow, TimeZoneId = "UTC" });
        await db.SaveChangesAsync();
        var controller = BuildController(db);
        controller.ControllerContext.HttpContext = CreateHttpContextWithUser("other");

        var result = await controller.DeleteAllUserLocations(user.Id);

        Assert.IsType<ForbidResult>(result);
        Assert.NotEmpty(db.Locations);
    }

    [Fact]
    [Trait("Category", "RequiresSpatialite")]
    public async Task DeleteAllUserLocations_RemovesRows_WhenCallerMatches()
    {
        var (db, connection) = CreateSqliteDb();
        await using var _ = connection;
        await using var __ = db;
        var user = TestDataFixtures.CreateUser(id: "target");
        db.Users.Add(user);
        db.Locations.Add(new AppLocation { UserId = user.Id, Coordinates = new NetTopologySuite.Geometries.Point(0, 0) { SRID = 4326 }, Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow, TimeZoneId = "UTC" });
        await db.SaveChangesAsync();
        var controller = BuildController(db);
        controller.ControllerContext.HttpContext = CreateHttpContextWithUser(user.Id);

        var result = await controller.DeleteAllUserLocations(user.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(db.Locations);
    }

    [Fact]
    public async Task GetPublicStats_ReturnsNotFound_WhenNoUser()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.GetPublicStats();

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetPublicStats_ReturnsStats_WhenUserPresent()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var statsService = new Mock<ILocationStatsService>();
        statsService.Setup(s => s.GetStatsForUserAsync(user.Id)).ReturnsAsync(new UserLocationStatsDto { TotalLocations = 3 });
        var controller = BuildController(db, statsService.Object);
        controller.ControllerContext.HttpContext = CreateHttpContextWithUser(user.Id);

        var result = await controller.GetPublicStats();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<UserLocationStatsDto>(ok.Value);
        Assert.Equal(3, dto.TotalLocations);
    }

    [Fact]
    public async Task GetDetailedStats_ReturnsNotFound_WhenUserMissing()
    {
        var controller = BuildController(CreateDbContext(), Mock.Of<ILocationStatsService>());

        var result = await controller.GetDetailedStats();

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetDetailedStats_ReturnsPayload_WhenUserPresent()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var statsDto = new UserLocationStatsDetailedDto { TotalLocations = 7 };
        var statsService = new Mock<ILocationStatsService>();
        statsService.Setup(s => s.GetDetailedStatsForUserAsync(user.Id)).ReturnsAsync(statsDto);

        var controller = BuildController(db, statsService.Object);
        controller.ControllerContext.HttpContext = CreateHttpContextWithUser(user.Id);

        var result = await controller.GetDetailedStats();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(statsDto, ok.Value);
    }

    [Fact]
    public async Task GetUserActivity_ReturnsUnauthorized_WhenUserMissing()
    {
        var controller = BuildController(CreateDbContext(), Mock.Of<ILocationStatsService>());

        var result = await controller.GetUserActivity();

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task GetUserActivity_ReturnsAggregatedActivity()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        var group = new Group { Id = Guid.NewGuid(), Name = "Alpha", OwnerUserId = user.Id };
        var invite = new GroupInvitation
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            InviteeUserId = user.Id,
            InviterUserId = user.Id,
            Token = Guid.NewGuid().ToString(),
            Status = GroupInvitation.InvitationStatuses.Pending,
            CreatedAt = DateTime.UtcNow
        };
        var joined = new GroupMember
        {
            GroupId = group.Id,
            UserId = user.Id,
            Status = GroupMember.MembershipStatuses.Active,
            JoinedAt = DateTime.UtcNow.AddHours(-1),
            Role = GroupMember.Roles.Member
        };
        var left = new GroupMember
        {
            GroupId = group.Id,
            UserId = user.Id,
            Status = GroupMember.MembershipStatuses.Left,
            LeftAt = DateTime.UtcNow.AddHours(-2),
            Role = GroupMember.Roles.Member
        };
        var removed = new GroupMember
        {
            GroupId = group.Id,
            UserId = user.Id,
            Status = GroupMember.MembershipStatuses.Removed,
            LeftAt = DateTime.UtcNow.AddHours(-3),
            Role = GroupMember.Roles.Member
        };

        db.Users.Add(user);
        db.Groups.Add(group);
        db.GroupInvitations.Add(invite);
        db.GroupMembers.AddRange(joined, left, removed);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        controller.ControllerContext.HttpContext = CreateHttpContextWithUser(user.Id);

        var result = await controller.GetUserActivity();

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        var invites = payload.GetType().GetProperty("invites")?.GetValue(payload) as IEnumerable<object>;
        var joinedList = payload.GetType().GetProperty("joined")?.GetValue(payload) as IEnumerable<object>;
        var removedList = payload.GetType().GetProperty("removed")?.GetValue(payload) as IEnumerable<object>;
        var leftList = payload.GetType().GetProperty("left")?.GetValue(payload) as IEnumerable<object>;

        Assert.NotNull(invites);
        Assert.Single(invites!);
        Assert.NotNull(joinedList);
        Assert.Single(joinedList!);
        Assert.NotNull(removedList);
        Assert.Single(removedList!);
        Assert.NotNull(leftList);
        Assert.Single(leftList!);
    }

    [Fact]
    public async Task Search_ReturnsEmpty_WhenQueryTooShort()
    {
        var db = CreateDbContext();
        var controller = BuildController(db);
        controller.ControllerContext.HttpContext = CreateHttpContextWithUser("u1");

        var result = await controller.Search("a", null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value);
        Assert.Empty(items);
    }

    [Fact]
    public async Task Search_ReturnsEmpty_WhenQueryIsNull()
    {
        var db = CreateDbContext();
        var controller = BuildController(db);
        controller.ControllerContext.HttpContext = CreateHttpContextWithUser("u1");

        var result = await controller.Search(null, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value);
        Assert.Empty(items);
    }

    [Fact]
    public async Task Search_ReturnsEmpty_WhenQueryIsWhitespace()
    {
        var db = CreateDbContext();
        var controller = BuildController(db);
        controller.ControllerContext.HttpContext = CreateHttpContextWithUser("u1");

        var result = await controller.Search("   ", null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value);
        Assert.Empty(items);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("x")]
    public async Task Search_ReturnsEmpty_WhenQueryInvalid(string query)
    {
        var db = CreateDbContext();
        var controller = BuildController(db);
        controller.ControllerContext.HttpContext = CreateHttpContextWithUser("u1");

        var result = await controller.Search(query, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value);
        Assert.Empty(items);
    }

    private UsersController BuildController(ApplicationDbContext db, ILocationStatsService? statsService = null)
    {
        return new UsersController(db, NullLogger<UsersController>.Instance, statsService ?? new LocationStatsService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static (ApplicationDbContext Db, SqliteConnection Connection) CreateSqliteDb()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection, o => o.UseNetTopologySuite())
            .ReplaceService<Microsoft.EntityFrameworkCore.Storage.IRelationalTypeMappingSource, SqliteGeographyTypeMappingSource>()
            .Options;
        var db = new SqliteApplicationDbContext(options, new ServiceCollection().BuildServiceProvider());
        db.Database.EnsureCreated();
        return (db, connection);
    }

    private sealed class SqliteApplicationDbContext : ApplicationDbContext
    {
        public SqliteApplicationDbContext(DbContextOptions<ApplicationDbContext> options, IServiceProvider sp) : base(options, sp)
        {
        }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            if (Database.IsSqlite())
            {
                builder.Entity<Location>()
                    .Property(l => l.Coordinates)
                    .HasConversion(
                        v => v == null ? null : v.AsText(),
                        v => string.IsNullOrEmpty(v) ? null! : (NetTopologySuite.Geometries.Point)new NetTopologySuite.IO.WKTReader().Read(v!))
                    .HasColumnType("TEXT");

                builder.Entity<TileCacheMetadata>()
                    .Property(t => t.TileLocation)
                    .HasConversion(
                        v => v == null ? null : v.AsText(),
                        v => string.IsNullOrEmpty(v) ? null! : (NetTopologySuite.Geometries.Point)new NetTopologySuite.IO.WKTReader().Read(v!))
                    .HasColumnType("TEXT");
            }
        }
    }

#pragma warning disable EF1001 // Internal EF Core API usage - necessary for SQLite geography type mapping in tests
    private sealed class SqliteGeographyTypeMappingSource : Microsoft.EntityFrameworkCore.Sqlite.Storage.Internal.SqliteTypeMappingSource
    {
        public SqliteGeographyTypeMappingSource(
            Microsoft.EntityFrameworkCore.Storage.TypeMappingSourceDependencies deps,
            Microsoft.EntityFrameworkCore.Storage.RelationalTypeMappingSourceDependencies relationalDeps)
            : base(deps, relationalDeps)
        {
        }

        protected override Microsoft.EntityFrameworkCore.Storage.RelationalTypeMapping? FindMapping(in Microsoft.EntityFrameworkCore.Storage.RelationalTypeMappingInfo mappingInfo)
        {
            if (mappingInfo.StoreTypeName != null &&
                mappingInfo.StoreTypeName.StartsWith("geography", StringComparison.OrdinalIgnoreCase))
            {
                var stringInfo = new Microsoft.EntityFrameworkCore.Storage.RelationalTypeMappingInfo(typeof(string));
                return base.FindMapping(stringInfo);
            }

            return base.FindMapping(mappingInfo);
        }
    }
#pragma warning restore EF1001
}
