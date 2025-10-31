using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests;

public class MobileApiControllerTests
{
    private static ApplicationDbContext MakeDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options, new ServiceCollection().BuildServiceProvider());
    }

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

    private static HttpContext CreateHttpContext(string? bearer = null)
    {
        var ctx = new DefaultHttpContext();
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            ctx.Request.Headers["Authorization"] = $"Bearer {bearer}";
        }
        return ctx;
    }

    [Fact]
    public async Task GetCurrentUserAsync_ReturnsUser_WhenTokenValid()
    {
        using var db = MakeDb();
        var user = new ApplicationUser { Id = "user1", UserName = "user", DisplayName = "User", IsActive = true };
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken
        {
            Name = "mobile",
            Token = "token-123",
            User = user,
            UserId = user.Id,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var httpContext = CreateHttpContext("token-123");
        var accessor = new MobileCurrentUserAccessor(new HttpContextAccessor { HttpContext = httpContext }, db);
        var controller = new TestController(db, accessor);
        controller.SetHttpContext(httpContext);

        var resolved = await controller.InvokeGetUserAsync();
        Assert.NotNull(resolved);
        Assert.Equal(user.Id, resolved!.Id);
    }

    [Fact]
    public async Task EnsureAuthenticatedUserAsync_ReturnsUnauthorized_WhenMissingToken()
    {
        using var db = MakeDb();
        var httpContext = CreateHttpContext();
        var accessor = new MobileCurrentUserAccessor(new HttpContextAccessor { HttpContext = httpContext }, db);
        var controller = new TestController(db, accessor);
        controller.SetHttpContext(httpContext);

        var (_, error) = await controller.InvokeEnsureAsync();
        Assert.IsType<UnauthorizedObjectResult>(error);
    }

    [Fact]
    public async Task EnsureAuthenticatedUserAsync_ReturnsForbid_WhenInactive()
    {
        using var db = MakeDb();
        var user = new ApplicationUser { Id = "user2", UserName = "inactive", DisplayName = "Inactive", IsActive = false };
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken
        {
            Name = "mobile",
            Token = "inactive-token",
            User = user,
            UserId = user.Id,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var httpContext = CreateHttpContext("inactive-token");
        var accessor = new MobileCurrentUserAccessor(new HttpContextAccessor { HttpContext = httpContext }, db);
        var controller = new TestController(db, accessor);
        controller.SetHttpContext(httpContext);

        var (resolved, error) = await controller.InvokeEnsureAsync();
        Assert.Null(resolved);
        Assert.IsType<ForbidResult>(error);
    }
}
