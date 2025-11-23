using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Base API controller role resolution.
/// </summary>
public class BaseApiControllerRoleTests : TestBase
{
    [Fact]
    public void GetUserRole_ReturnsRole_WhenClaimExists()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { Token = "tok", UserId = user.Id, Name = "test", User = user });
        db.UserClaims.Add(new IdentityUserClaim<string> { UserId = user.Id, ClaimType = ClaimTypes.Role, ClaimValue = "User" });
        db.SaveChanges();
        var controller = new StubBaseApiController(db);
        controller.ControllerContext.HttpContext.Request.Headers["Authorization"] = "Bearer tok";

        var role = controller.InvokeGetUserRole();

        Assert.Equal("User", role);
    }

    [Fact]
    public void GetUserRole_ReturnsNull_WhenNoToken()
    {
        var controller = new StubBaseApiController(CreateDbContext());

        var role = controller.InvokeGetUserRole();

        Assert.Null(role);
    }

    private sealed class StubBaseApiController : BaseApiController
    {
        public StubBaseApiController(ApplicationDbContext db)
            : base(db, NullLogger<BaseApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        }

        public string? InvokeGetUserRole() => GetUserRole();
    }
}
