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

namespace Wayfarer.Tests.Integration;

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
    public async Task LocationSseBroadcasts_CheckInBroadcastsToUserAndGroupChannels()
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

        // Should have 1 per-user broadcast + 2 group broadcasts = 3 total
        Assert.Equal(3, sse.Messages.Count);

        // Verify per-user broadcast for timeline views
        var userChannel = $"location-update-{user.UserName}";
        var userMessage = Assert.Single(sse.Messages, m => m.Channel == userChannel);
        using (var payload = JsonDocument.Parse(userMessage.Data))
        {
            var root = payload.RootElement;
            Assert.True(root.TryGetProperty("locationId", out _));
            Assert.True(root.TryGetProperty("timestampUtc", out _));
            Assert.Equal(user.Id, root.GetProperty("userId").GetString());
            Assert.Equal(user.UserName, root.GetProperty("userName").GetString());
            Assert.True(root.GetProperty("isLive").GetBoolean());
            Assert.Equal("check-in", root.GetProperty("type").GetString());
        }

        // Verify group broadcasts
        var groupMessages = sse.Messages.Where(m => m.Channel.StartsWith("group-", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, groupMessages.Count);

        foreach (var message in groupMessages)
        {
            using var groupPayload = JsonDocument.Parse(message.Data);
            var root = groupPayload.RootElement;
            Assert.Equal("location", root.GetProperty("type").GetString());
            Assert.True(root.TryGetProperty("locationId", out _));
            Assert.True(root.TryGetProperty("timestampUtc", out _));
            Assert.Equal(user.Id, root.GetProperty("userId").GetString());
            Assert.Equal(user.UserName, root.GetProperty("userName").GetString());
            Assert.True(root.GetProperty("isLive").GetBoolean());
            Assert.Equal("check-in", root.GetProperty("locationType").GetString());
        }
    }

    [Fact]
    [Trait("Category", "LocationSseBroadcasts")]
    public async Task LocationSseBroadcasts_LogLocationBroadcastsToGroupChannel()
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

        // Should have 1 per-user broadcast + 1 group broadcast = 2 total
        Assert.Equal(2, sse.Messages.Count);

        // Verify per-user broadcast for timeline views
        var userChannel = $"location-update-{user.UserName}";
        var userMessage = Assert.Single(sse.Messages, m => m.Channel == userChannel);
        using (var payload = JsonDocument.Parse(userMessage.Data))
        {
            var root = payload.RootElement;
            Assert.True(root.TryGetProperty("locationId", out _));
            Assert.True(root.TryGetProperty("timestampUtc", out _));
            Assert.Equal(user.Id, root.GetProperty("userId").GetString());
            Assert.Equal(user.UserName, root.GetProperty("userName").GetString());
            Assert.True(root.GetProperty("isLive").GetBoolean());
            // type should be null for non-check-in locations
            Assert.True(root.TryGetProperty("type", out var typeVal) && typeVal.ValueKind == JsonValueKind.Null);
        }

        // Verify group broadcast
        var groupMessage = Assert.Single(sse.Messages, m => m.Channel.StartsWith("group-", StringComparison.Ordinal));
        using (var groupPayload = JsonDocument.Parse(groupMessage.Data))
        {
            var root = groupPayload.RootElement;
            Assert.Equal("location", root.GetProperty("type").GetString());
            Assert.True(root.TryGetProperty("locationId", out _));
            Assert.Equal(user.Id, root.GetProperty("userId").GetString());
            Assert.Equal(user.UserName, root.GetProperty("userName").GetString());
            Assert.True(root.GetProperty("isLive").GetBoolean());
            // locationType should be null for non-check-in locations
            Assert.False(root.TryGetProperty("locationType", out var lt) && lt.ValueKind != JsonValueKind.Null);
        }
    }
}
