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

    [Fact]
    public async Task Attach_ReturnsBadRequest_WhenNoTagsProvided()
    {
        var controller = BuildController(CreateDbContext(), Mock.Of<ITripTagService>(), "user-1");

        var result = await controller.Attach(Guid.NewGuid(), new TripTagsController.ModifyTagsRequest(), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Please provide at least one tag", bad.Value!.ToString());
    }

    [Fact]
    public async Task Attach_ReturnsUnauthorized_WhenNoUser()
    {
        var controller = BuildController(CreateDbContext(), Mock.Of<ITripTagService>(), userId: null);
        var request = new TripTagsController.ModifyTagsRequest { Tags = new List<string> { "trail" } };

        var result = await controller.Attach(Guid.NewGuid(), request, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Attach_ReturnsBadRequest_OnValidationException()
    {
        var tripId = Guid.NewGuid();
        var service = new Mock<ITripTagService>();
        service.Setup(s => s.AttachTagsAsync(tripId, It.IsAny<IEnumerable<string>>(), "user-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.ComponentModel.DataAnnotations.ValidationException("nope"));
        var controller = BuildController(CreateDbContext(), service.Object, "user-1");
        var request = new TripTagsController.ModifyTagsRequest { Tags = new List<string> { "trail" } };

        var result = await controller.Attach(tripId, request, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("nope", bad.Value!.ToString());
    }

    [Fact]
    public async Task Attach_ReturnsNotFound_OnMissingTrip()
    {
        var tripId = Guid.NewGuid();
        var service = new Mock<ITripTagService>();
        service.Setup(s => s.AttachTagsAsync(tripId, It.IsAny<IEnumerable<string>>(), "user-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("missing"));
        var controller = BuildController(CreateDbContext(), service.Object, "user-1");
        var request = new TripTagsController.ModifyTagsRequest { Tags = new List<string> { "trail" } };

        var result = await controller.Attach(tripId, request, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Contains("missing", notFound.Value!.ToString());
    }

    [Fact]
    public async Task Attach_ReturnsOk_WithTags()
    {
        var tripId = Guid.NewGuid();
        var service = new Mock<ITripTagService>();
        service.Setup(s => s.AttachTagsAsync(tripId, It.IsAny<IEnumerable<string>>(), "user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TripTagDto>
            {
                new(Guid.NewGuid(), "trail", "trail"),
                new(Guid.NewGuid(), "evening", "evening")
            });
        var controller = BuildController(CreateDbContext(), service.Object, "user-1");
        var request = new TripTagsController.ModifyTagsRequest { Tags = new List<string> { "trail" } };

        var result = await controller.Attach(tripId, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var tags = Assert.IsAssignableFrom<IEnumerable<TripTagDto>>(ok.Value!.GetType().GetProperty("tags")!.GetValue(ok.Value)!);
        Assert.Equal(new[] { "trail", "evening" }, tags.Select(t => t.Name));
    }

    [Fact]
    public async Task Remove_ReturnsUnauthorized_WhenNoUser()
    {
        var controller = BuildController(CreateDbContext(), Mock.Of<ITripTagService>(), userId: null);

        var result = await controller.Remove(Guid.NewGuid(), "slug", CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Remove_ReturnsNotFound_WhenServiceSaysMissing()
    {
        var tripId = Guid.NewGuid();
        var service = new Mock<ITripTagService>();
        service.Setup(s => s.DetachTagAsync(tripId, "slug", "user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var controller = BuildController(CreateDbContext(), service.Object, "user-1");

        var result = await controller.Remove(tripId, "slug", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Remove_ReturnsOk_WithUpdatedTags()
    {
        var tripId = Guid.NewGuid();
        var service = new Mock<ITripTagService>();
        service.Setup(s => s.DetachTagAsync(tripId, "slug", "user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        service.Setup(s => s.GetTagsForTripAsync(tripId, "user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TripTagDto>
            {
                new(Guid.NewGuid(), "left", "left")
            });
        var controller = BuildController(CreateDbContext(), service.Object, "user-1");

        var result = await controller.Remove(tripId, "slug", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var tags = Assert.IsAssignableFrom<IEnumerable<TripTagDto>>(ok.Value!.GetType().GetProperty("tags")!.GetValue(ok.Value)!);
        Assert.Equal(new[] { "left" }, tags.Select(t => t.Name));
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
