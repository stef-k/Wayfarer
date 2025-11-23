using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Base API token parsing.
/// </summary>
public class BaseApiControllerTests : TestBase
{
    [Fact]
    public void GetUserFromToken_ReturnsNull_WhenMissing()
    {
        var db = CreateDbContext();
        var controller = new StubBaseApiController(db);

        var user = controller.InvokeGetUser();

        Assert.Null(user);
    }

    [Fact]
    public void GetUserFromToken_ReturnsUser_WhenTokenMatches()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { Token = "tok", UserId = user.Id, Name = "test", User = user });
        db.SaveChanges();
        var controller = new StubBaseApiController(db);
        controller.ControllerContext.HttpContext.Request.Headers["Authorization"] = "Bearer tok";

        var found = controller.InvokeGetUser();

        Assert.NotNull(found);
        Assert.Equal("u1", found!.Id);
    }

    private sealed class StubBaseApiController : BaseApiController
    {
        public StubBaseApiController(ApplicationDbContext db)
            : base(db, NullLogger<BaseApiController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
        }

        public ApplicationUser? InvokeGetUser() => GetUserFromToken();
    }
}
