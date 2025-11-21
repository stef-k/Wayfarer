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
                        v => string.IsNullOrEmpty(v) ? null : (NetTopologySuite.Geometries.Point)new NetTopologySuite.IO.WKTReader().Read(v!))
                    .HasColumnType("TEXT");

                builder.Entity<TileCacheMetadata>()
                    .Property(t => t.TileLocation)
                    .HasConversion(
                        v => v == null ? null : v.AsText(),
                        v => string.IsNullOrEmpty(v) ? null : (NetTopologySuite.Geometries.Point)new NetTopologySuite.IO.WKTReader().Read(v!))
                    .HasColumnType("TEXT");
            }
        }
    }

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
}
