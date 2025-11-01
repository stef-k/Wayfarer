using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests;

public class LocationSseBroadcastsTests
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

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"features\":[]}")
            };
            return Task.FromResult(response);
        }
    }

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options, new ServiceCollection().BuildServiceProvider());
    }

    private static async Task SeedUserAsync(ApplicationDbContext db, ApplicationUser user, string tokenValue, params Group[] groups)
    {
        db.Users.Add(user);

        foreach (var group in groups)
        {
            db.Groups.Add(group);
            db.GroupMembers.Add(new GroupMember
            {
                GroupId = group.Id,
                UserId = user.Id,
                Status = GroupMember.MembershipStatuses.Active,
                JoinedAt = DateTime.UtcNow
            });
        }

        var token = new ApiToken
        {
            Name = "mobile",
            Token = tokenValue,
            CreatedAt = DateTime.UtcNow,
            UserId = user.Id,
            User = user
        };

        user.ApiTokens.Add(token);
        db.ApiTokens.Add(token);

        db.ApplicationSettings.Add(new ApplicationSettings
        {
            Id = 1,
            LocationTimeThresholdMinutes = 5,
            LocationDistanceThresholdMeters = 15
        });

        await db.SaveChangesAsync();
    }

    private static (LocationController Controller, TestSseService Sse) CreateController(ApplicationDbContext db, string tokenValue)
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var sse = new TestSseService();
        var settingsService = new ApplicationSettingsService(db, memoryCache);
        var reverseGeocoding = new ReverseGeocodingService(new HttpClient(new FakeHttpMessageHandler()), NullLogger<BaseApiController>.Instance);
        var locationService = new LocationService(db);
        var statsService = new LocationStatsService(db);

        var controller = new LocationController(
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
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        controller.ControllerContext.HttpContext!.Request.Headers["Authorization"] = $"Bearer {tokenValue}";
        return (controller, sse);
    }

    [Fact]
    [Trait("Category", "LocationSseBroadcasts")]
    public async Task LocationSseBroadcasts_CheckInBroadcastsEnrichedPayload()
    {
        using var db = CreateDb();
        var token = "token-checkin";
        var user = new ApplicationUser
        {
            Id = "user-checkin",
            UserName = "alice",
            DisplayName = "Alice",
            Email = "alice@example.com",
            IsActive = true,
            EmailConfirmed = true
        };

        var firstGroup = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Friends",
            GroupType = "Friends",
            OwnerUserId = user.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var secondGroup = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Org",
            GroupType = "Organization",
            OwnerUserId = user.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await SeedUserAsync(db, user, token, firstGroup, secondGroup);
        var (controller, sse) = CreateController(db, token);

        var dto = new GpsLoggerLocationDto
        {
            Latitude = 37.978,
            Longitude = 23.72,
            Timestamp = DateTime.UtcNow
        };

        var result = await controller.CheckIn(dto);
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);

        var userChannel = $"location-update-{user.UserName}";
        var userMessage = Assert.Single(sse.Messages.Where(m => m.Channel == userChannel));

        using (var payload = JsonDocument.Parse(userMessage.Data))
        {
            var root = payload.RootElement;
            Assert.True(root.TryGetProperty("LocationId", out var legacyId));
            Assert.True(root.TryGetProperty("locationId", out var modernId));
            Assert.Equal(legacyId.GetInt32(), modernId.GetInt32());

            Assert.True(root.TryGetProperty("TimeStamp", out var legacyTimestamp));
            Assert.True(root.TryGetProperty("timestampUtc", out var modernTimestamp));
            Assert.Equal(legacyTimestamp.GetDateTime(), modernTimestamp.GetDateTime());

            Assert.Equal(user.Id, root.GetProperty("userId").GetString());
            Assert.Equal(user.UserName, root.GetProperty("userName").GetString());
            Assert.True(root.GetProperty("isLive").GetBoolean());
            Assert.Equal("check-in", root.GetProperty("Type").GetString());
        }

        var groupMessages = sse.Messages.Where(m => m.Channel.StartsWith("group-location-update-", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, groupMessages.Count);
        foreach (var message in groupMessages)
        {
            Assert.Equal(userMessage.Data, message.Data);
        }
    }

    [Fact]
    [Trait("Category", "LocationSseBroadcasts")]
    public async Task LocationSseBroadcasts_LogLocationOmitsCheckInType()
    {
        using var db = CreateDb();
        var token = "token-log";
        var user = new ApplicationUser
        {
            Id = "user-log",
            UserName = "bob",
            DisplayName = "Bob",
            Email = "bob@example.com",
            IsActive = true,
            EmailConfirmed = true
        };

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Explorers",
            GroupType = "Friends",
            OwnerUserId = user.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await SeedUserAsync(db, user, token, group);
        var (controller, sse) = CreateController(db, token);

        var dto = new GpsLoggerLocationDto
        {
            Latitude = 51.5,
            Longitude = -0.12,
            Timestamp = DateTime.UtcNow
        };

        var result = await controller.LogLocation(dto);
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);

        var userChannel = $"location-update-{user.UserName}";
        var userMessage = Assert.Single(sse.Messages.Where(m => m.Channel == userChannel));

        using (var payload = JsonDocument.Parse(userMessage.Data))
        {
            var root = payload.RootElement;
            Assert.Equal(root.GetProperty("LocationId").GetInt32(), root.GetProperty("locationId").GetInt32());
            Assert.Equal(root.GetProperty("TimeStamp").GetDateTime(), root.GetProperty("timestampUtc").GetDateTime());
            Assert.Equal(user.Id, root.GetProperty("userId").GetString());
            Assert.Equal(user.UserName, root.GetProperty("userName").GetString());
            Assert.True(root.GetProperty("isLive").GetBoolean());
            Assert.False(root.TryGetProperty("Type", out _));
        }

        var groupMessage = Assert.Single(sse.Messages.Where(m => m.Channel.StartsWith("group-location-update-", StringComparison.Ordinal)));
        Assert.Equal(userMessage.Data, groupMessage.Data);
    }
}
