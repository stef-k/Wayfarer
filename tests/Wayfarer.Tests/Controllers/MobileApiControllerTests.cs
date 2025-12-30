using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Tests for the MobileApiController base class authentication functionality.
/// </summary>
public class MobileApiControllerTests : TestBase
{
    /// <summary>
    /// Test controller that exposes protected methods for testing.
    /// </summary>
    private sealed class TestController : MobileApiController
    {
        public TestController(ApplicationDbContext db, IMobileCurrentUserAccessor accessor)
            : base(db, NullLogger<BaseApiController>.Instance, accessor)
        {
        }

        public void SetHttpContext(HttpContext context)
        {
            ControllerContext = new ControllerContext { HttpContext = context };
        }

        public Task<ApplicationUser?> InvokeGetUserAsync(CancellationToken ct = default) => GetCurrentUserAsync(ct);
        public Task<(ApplicationUser? user, IActionResult? error)> InvokeEnsureAsync(CancellationToken ct = default) => EnsureAuthenticatedUserAsync(ct);
    }

    [Fact]
    public async Task GetCurrentUserAsync_ReturnsUser_WhenTokenValid()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "user1");
        db.Users.Add(user);

        var token = TestDataFixtures.CreateApiToken(user, "token-123");
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync();

        var httpContext = CreateHttpContext("token-123");
        var accessor = new MobileCurrentUserAccessor(new HttpContextAccessor { HttpContext = httpContext }, db, NullLogger<MobileCurrentUserAccessor>.Instance);
        var controller = new TestController(db, accessor);
        controller.SetHttpContext(httpContext);

        // Act
        var resolved = await controller.InvokeGetUserAsync();

        // Assert
        Assert.NotNull(resolved);
        Assert.Equal(user.Id, resolved!.Id);
    }

    [Fact]
    public async Task EnsureAuthenticatedUserAsync_ReturnsUnauthorized_WhenMissingToken()
    {
        // Arrange
        var db = CreateDbContext();
        var httpContext = CreateHttpContext();
        var accessor = new MobileCurrentUserAccessor(new HttpContextAccessor { HttpContext = httpContext }, db, NullLogger<MobileCurrentUserAccessor>.Instance);
        var controller = new TestController(db, accessor);
        controller.SetHttpContext(httpContext);

        // Act
        var (_, error) = await controller.InvokeEnsureAsync();

        // Assert
        Assert.IsType<UnauthorizedObjectResult>(error);
    }

    [Fact]
    public async Task EnsureAuthenticatedUserAsync_ReturnsForbid_WhenInactive()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "user2", isActive: false);
        db.Users.Add(user);

        var token = TestDataFixtures.CreateApiToken(user, "inactive-token");
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync();

        var httpContext = CreateHttpContext("inactive-token");
        var accessor = new MobileCurrentUserAccessor(new HttpContextAccessor { HttpContext = httpContext }, db, NullLogger<MobileCurrentUserAccessor>.Instance);
        var controller = new TestController(db, accessor);
        controller.SetHttpContext(httpContext);

        // Act
        var (resolved, error) = await controller.InvokeEnsureAsync();

        // Assert
        Assert.Null(resolved);
        Assert.IsType<ForbidResult>(error);
    }
}
