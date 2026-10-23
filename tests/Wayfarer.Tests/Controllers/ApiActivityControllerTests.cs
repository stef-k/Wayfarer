using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wayfarer.Services;
using Wayfarer.Util;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// API Activity types listing.
/// </summary>
[Collection("API token singleton admission")]
public class ApiActivityControllerTests : TestBase
{
    [Fact]
    public void GetActivityTypes_ReturnsOrderedActivities()
    {
        var db = CreateDbContext();
        db.ActivityTypes.AddRange(
            new ActivityType { Id = 2, Name = "Walk" },
            new ActivityType { Id = 1, Name = "Bike" });
        db.SaveChanges();
        db.Users.Add(TestDataFixtures.CreateUser(id: "catalog-reader"));
        db.SaveChanges();
        var controller = new ActivityController(db, NullLogger<BaseApiController>.Instance);

        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser("catalog-reader", "User") };
        var result = controller.GetActivityTypes();

        var ok = Assert.IsType<OkObjectResult>(result);
        var list = Assert.IsAssignableFrom<IEnumerable<ActivityTypeDto>>(ok.Value);
        Assert.Equal(new[] { "Bike", "Walk" }, list.Select(a => a.Name).ToArray());
    }

    /// <summary>Real Identity cookies and current opaque Bearer requests retain the exact catalog wire contract.</summary>
    [Theory]
    [InlineData("cookie", true)]
    [InlineData("bearer", true)]
    [InlineData("anonymous", false)]
    [InlineData("invalid", false)]
    [InlineData("inactive-cookie", false)]
    [InlineData("inactive-bearer", false)]
    [InlineData("inactive-cookie-valid-bearer", false)]
    public async Task RoutedCatalogAuthentication(string mode, bool succeeds)
    {
        using var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "catalog-user", isActive: !mode.StartsWith("inactive"));
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { User = user, UserId = user.Id, Name = "phone",
            Token = "", TokenHash = ApiTokenService.HashToken("mobile-token") });
        var active = TestDataFixtures.CreateUser(id: "other-active-catalog-user");
        db.Users.Add(active);
        db.ApiTokens.Add(new ApiToken { User = active, UserId = active.Id, Name = "phone", Token = "other-active-token" });
        db.ActivityTypes.AddRange(new ActivityType { Id = 2, Name = "Walking", Description = "Walk" },
            new ActivityType { Id = 1, Name = "GPS" });
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        if (mode.Contains("cookie"))
        {
            client.DefaultRequestHeaders.Add("Cookie", IdentityRouteHost.Cookie(app.Services, user.Id, "User"));
            // An authenticated cookie owns resolution, even when the Bearer is invalid.
            client.DefaultRequestHeaders.Authorization = new("Bearer", mode == "inactive-cookie-valid-bearer" ? "other-active-token" : "invalid");
        }
        else if (mode != "anonymous")
            client.DefaultRequestHeaders.Authorization = new("Bearer", mode == "invalid" ? "invalid" : "mobile-token");
        using var response = await client.GetAsync("/api/activity");
        Assert.Equal(succeeds ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
        if (!succeeds) { Assert.DoesNotContain("Walking", body); return; }
        using var document = JsonDocument.Parse(body);
        var rows = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(new[] { "GPS", "Walking" }, rows.Select(r => r.GetProperty("name").GetString()));
        Assert.All(rows, row => Assert.Equal(new[] { "description", "id", "name" }, row.EnumerateObject().Select(p => p.Name).Order()));
        Assert.Equal(1, rows[0].GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("description").ValueKind);
        Assert.Equal("Walk", rows[1].GetProperty("description").GetString());
    }

    /// <summary>The production MVC filter translates catalog lookup saturation rather than returning 401.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoutedCatalogPreservesAdmissionOverload(bool global)
    {
        using var db = CreateDbContext();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "anything");
        var held = new List<IDisposable>();
        try
        {
            for (var i = 0; i < (global ? 32 : 16); i++)
                held.Add(ApiWorkAdmission.TokenLookups.TryAcquire(global ? $"catalog-{i}" : "192.0.2.20", out _)!);
            using var response = await client.GetAsync("/api/activity");
            Assert.Equal(global ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter?.Delta);
        }
        finally { foreach (var permit in held) permit.Dispose(); }
    }
}
