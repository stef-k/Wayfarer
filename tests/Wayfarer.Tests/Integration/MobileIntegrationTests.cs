using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Options;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Xunit;
using LocationEntity = Wayfarer.Models.Location;

namespace Wayfarer.Tests.Integration;

public class MobileIntegrationTests
{
    private sealed record IntegrationContext(
        ApplicationDbContext Db,
        MobileGroupsController GroupsController,
        MobileSseController SseController,
        LocationController LocationController,
        SseService SseService,
        HttpContext HttpContext);

    private static IntegrationContext CreateContext()
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

        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        db.ApplicationSettings.Add(new ApplicationSettings { LocationTimeThresholdMinutes = 5, LocationDistanceThresholdMeters = 15 });
        db.SaveChanges();

        SeedData(db);

        var httpContext = new DefaultHttpContext();
        var accessor = new MobileCurrentUserAccessor(new HttpContextAccessor { HttpContext = httpContext }, db);

        var userColor = new UserColorService();
        var locationService = new LocationService(db);
        var timelineService = new GroupTimelineService(db, locationService, configuration);
        var sse = new SseService();
        var sseOptions = new MobileSseOptions { HeartbeatIntervalMilliseconds = 100 };

        var groupsController = new MobileGroupsController(db, NullLogger<BaseApiController>.Instance, accessor, userColor, timelineService)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var sseController = new MobileSseController(db, NullLogger<BaseApiController>.Instance, accessor, sse, timelineService, sseOptions)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var settingsService = new ApplicationSettingsService(db, memoryCache);
        var statsService = new LocationStatsService(db);
        var httpClient = new HttpClient(new FakeMessageHandler());
        var reverseGeocoding = new ReverseGeocodingService(httpClient, NullLogger<BaseApiController>.Instance);

        var locationController = new LocationController(
            db,
            NullLogger<BaseApiController>.Instance,
            memoryCache,
            settingsService,
            reverseGeocoding,
            locationService,
            sse,
            statsService,
            locationService)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        return new IntegrationContext(db, groupsController, sseController, locationController, sse, httpContext);
    }

    private static void SeedData(ApplicationDbContext db)
    {
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Explorers",
            GroupType = "Friends",
            OwnerUserId = "caller",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var caller = new ApplicationUser { Id = "caller", UserName = "caller", DisplayName = "Caller", IsActive = true };
        var friend = new ApplicationUser { Id = "friend", UserName = "friend", DisplayName = "Friend", IsActive = true };

        db.Users.AddRange(caller, friend);
        db.Groups.Add(group);
        db.ApiTokens.Add(new ApiToken { Name = "mobile", Token = "token", UserId = caller.Id, User = caller, CreatedAt = DateTime.UtcNow });

        db.GroupMembers.AddRange(
            new GroupMember { GroupId = group.Id, UserId = caller.Id, Role = GroupMember.Roles.Owner, Status = GroupMember.MembershipStatuses.Active, JoinedAt = DateTime.UtcNow },
            new GroupMember { GroupId = group.Id, UserId = friend.Id, Role = GroupMember.Roles.Member, Status = GroupMember.MembershipStatuses.Active, JoinedAt = DateTime.UtcNow });

        db.Locations.AddRange(
            new LocationEntity { UserId = caller.Id, Coordinates = new Point(10, 10) { SRID = 4326 }, Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow, TimeZoneId = "UTC" },
            new LocationEntity { UserId = friend.Id, Coordinates = new Point(11, 11) { SRID = 4326 }, Timestamp = DateTime.UtcNow.AddMinutes(-1), LocalTimestamp = DateTime.UtcNow.AddMinutes(-1), TimeZoneId = "UTC" });

        db.SaveChanges();
    }

    [Fact]
    [Trait("Category", "MobileIntegration")]
    public async Task MobileIntegration_EndToEnd_GroupEndpoints()
    {
        var ctx = CreateContext();
        ctx.HttpContext.Request.Headers["Authorization"] = "Bearer token";

        var groupsResponse = await ctx.GroupsController.Get("all", CancellationToken.None);
        var groups = Assert.IsAssignableFrom<IEnumerable<MobileGroupSummaryDto>>(Assert.IsType<OkObjectResult>(groupsResponse).Value);
        Assert.Single(groups);

        var groupId = groups.First().Id;

        var membersResponse = await ctx.GroupsController.Members(groupId, CancellationToken.None);
        var members = Assert.IsAssignableFrom<IEnumerable<GroupMemberDto>>(Assert.IsType<OkObjectResult>(membersResponse).Value);
        Assert.Equal(2, members.Count());

        var latestResponse = await ctx.GroupsController.Latest(groupId, new GroupLocationsLatestRequest(), CancellationToken.None);
        var latest = Assert.IsAssignableFrom<IEnumerable<PublicLocationDto>>(Assert.IsType<OkObjectResult>(latestResponse).Value);
        Assert.Equal(2, latest.Count());

        var queryResponse = await ctx.GroupsController.Query(groupId, new GroupLocationsQueryRequest
        {
            MinLng = -180,
            MinLat = -90,
            MaxLng = 180,
            MaxLat = 90,
            ZoomLevel = 10
        }, CancellationToken.None);
        var queryPayload = Assert.IsType<GroupLocationsQueryResponse>(Assert.IsType<OkObjectResult>(queryResponse).Value);
        Assert.Equal(2, queryPayload.TotalItems);
        Assert.False(queryPayload.HasMore);
    }

    [Fact]
    [Trait("Category", "MobileIntegration")]
    public async Task MobileIntegration_SseHeartbeatAndPayload()
    {
        var ctx = CreateContext();
        ctx.HttpContext.Request.Headers["Authorization"] = "Bearer token";
        ctx.HttpContext.Response.Body = new MemoryStream();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var subscribeTask = ctx.SseController.SubscribeToUserAsync("caller", cts.Token);
        await Task.Delay(50);

        var checkInDto = new GpsLoggerLocationDto
        {
            Latitude = 12.34,
            Longitude = 23.45,
            Timestamp = DateTime.UtcNow
        };

        var checkIn = await ctx.LocationController.CheckIn(checkInDto);
        Assert.IsType<OkObjectResult>(checkIn);

        await Task.Delay(100);
        cts.Cancel();
        await subscribeTask;

        ctx.HttpContext.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.HttpContext.Response.Body).ReadToEndAsync();
        // SSE heartbeat: ":" followed by blank line - accept both LF and CRLF line endings
        Assert.Matches(@":\r?\n\r?\n", text);
        Assert.Contains("\"userId\":\"caller\"", text);
        Assert.Contains("\"userName\":\"caller\"", text);
        Assert.Contains("\"locationId\"", text);
        Assert.Contains("\"timestampUtc\"", text);
        Assert.Contains("\"Type\":\"check-in\"", text);
    }

    private sealed class FakeMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"features\":[]}")
            });
        }
    }
}


