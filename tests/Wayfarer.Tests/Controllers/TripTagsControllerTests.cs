using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Models.Dtos;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Covers TripTagsController API surface.
/// </summary>
public class TripTagsControllerTests : TestBase
{
    [Fact]
    public async Task Get_ReturnsTags_ForAuthenticatedUser()
    {
        var tripId = Guid.NewGuid();
        var db = CreateDbContext();
        var service = new Mock<ITripTagService>();
        service.Setup(s => s.GetTagsForTripAsync(tripId, "user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TripTagDto>
            {
                new(Guid.NewGuid(), "hiking", "hiking"),
                new(Guid.NewGuid(), "sunrise", "sunrise")
            });
        var controller = BuildController(db, service.Object, "user-1");

        var result = await controller.Get(tripId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var tags = Assert.IsAssignableFrom<IEnumerable<TripTagDto>>(ok.Value!.GetType().GetProperty("tags")!.GetValue(ok.Value)!);
        Assert.Equal(new[] { "hiking", "sunrise" }, tags.Select(t => t.Name));
    }

    [Fact]
    public async Task Get_ReturnsUnauthorized_WhenNoUser()
    {
        var controller = BuildController(CreateDbContext(), Mock.Of<ITripTagService>(), userId: null);

        var result = await controller.Get(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
    }

    private static TripTagsController BuildController(ApplicationDbContext db, ITripTagService service, string? userId)
    {
        var logger = NullLogger<TripTagsController>.Instance;
        var controller = new TripTagsController(logger, db, service);
        var http = new DefaultHttpContext
        {
            User = userId == null
                ? new ClaimsPrincipal(new ClaimsIdentity())
                : new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, userId)
                }, "TestAuth"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }
}
