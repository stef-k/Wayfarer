using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Trip export controller coverage.
/// </summary>
public class TripExportControllerTests : TestBase
{
    [Fact]
    public async Task ExportWayfarerKml_ReturnsFile_ForOwner()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        db.Trips.Add(new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip", IsPublic = false });
        await db.SaveChangesAsync();

        var exportSvc = new Mock<ITripExportService>();
        exportSvc.Setup(s => s.GenerateWayfarerKml(It.IsAny<Guid>())).Returns("<kml/>");
        var controller = BuildController(db, user, exportSvc.Object);

        var result = await controller.ExportWayfarerKml(db.Trips.Single().Id);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/vnd.google-earth.kml+xml", file.ContentType);
    }

    [Fact]
    public async Task ExportWayfarerKml_ForbidForPrivateNonOwner()
    {
        var db = CreateDbContext();
        db.Trips.Add(new Trip { Id = Guid.NewGuid(), UserId = "owner", Name = "Trip", IsPublic = false });
        await db.SaveChangesAsync();
        var controller = BuildController(db, TestDataFixtures.CreateUser(id: "other"), Mock.Of<ITripExportService>());

        var result = await controller.ExportWayfarerKml(db.Trips.Single().Id);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task ExportPdf_NotFoundWhenMissing()
    {
        var controller = BuildController(CreateDbContext(), TestDataFixtures.CreateUser(id: "u1"), Mock.Of<ITripExportService>());

        var result = await controller.ExportPdf(Guid.NewGuid(), null, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task ExportPdf_ReturnsFile_ForPublicTrip()
    {
        var db = CreateDbContext();
        db.Trips.Add(new Trip { Id = Guid.NewGuid(), UserId = "owner", Name = "Trip", IsPublic = true });
        await db.SaveChangesAsync();
        var exportSvc = new Mock<ITripExportService>();
        exportSvc.Setup(s => s.GeneratePdfGuideAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[] { 1, 2, 3 }));
        var controller = BuildController(db, TestDataFixtures.CreateUser(id: "viewer"), exportSvc.Object);

        var result = await controller.ExportPdf(db.Trips.Single().Id, "sess", CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
    }

    /// <summary>Progress subscriptions retain the public-trip or authenticated-owner boundary.</summary>
    [Theory]
    [InlineData(true, false, 200)]
    [InlineData(false, true, 200)]
    [InlineData(false, false, 403)]
    public async Task ExportProgress_RetainsPublicOrOwnerBoundary(bool isPublic, bool isOwner, int status)
    {
        using var db = CreateDbContext();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "owner", Name = "Trip", IsPublic = isPublic };
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        var controller = BuildController(db, TestDataFixtures.CreateUser(id: isOwner ? "owner" : "other"),
            Mock.Of<ITripExportService>(), new SseService());
        controller.Response.Body = new MemoryStream();
        if (!isOwner) controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        using var cts = new CancellationTokenSource();
        var stream = controller.ExportProgress(trip.Id, "synthetic-session", cts.Token);
        try
        {
            Assert.Equal(status, controller.Response.StatusCode);
            Assert.Equal(status == 200 ? "text/event-stream" : null, controller.Response.ContentType);
        }
        finally
        {
            cts.Cancel();
            await stream;
        }
    }

    private static TripExportController BuildController(ApplicationDbContext db, ApplicationUser user, ITripExportService exportSvc, SseService? sse = null)
    {
        var controller = new TripExportController(
            NullLogger<BaseController>.Instance,
            db,
            exportSvc,
            sse ?? Mock.Of<SseService>());
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id),
                new Claim(ClaimTypes.Name, user.UserName ?? "user")
            }, "TestAuth"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }
}
