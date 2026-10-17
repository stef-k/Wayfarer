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
    /// <summary>Both KML formats preserve their payload, MIME and attachment name for owners and public viewers.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Kml_ReturnsFile_ForOwnerOrPublicViewer(bool google, bool isPublic)
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        db.Trips.Add(new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip", IsPublic = isPublic });
        await db.SaveChangesAsync();

        var exportSvc = new Mock<ITripExportService>();
        exportSvc.Setup(s => s.GenerateWayfarerKml(It.IsAny<Guid>())).Returns("<kml/>");
        exportSvc.Setup(s => s.GenerateGoogleMyMapsKml(It.IsAny<Guid>())).Returns("<kml/>");
        var controller = BuildController(db, isPublic ? TestDataFixtures.CreateUser(id: "other") : user, exportSvc.Object);

        var result = google ? await controller.ExportGoogleMyMapsKml(db.Trips.Single().Id)
            : await controller.ExportWayfarerKml(db.Trips.Single().Id);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/vnd.google-earth.kml+xml", file.ContentType);
        Assert.Equal($"Trip-{(google ? "Google" : "Wayfarer")}.kml", file.FileDownloadName);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("<kml/>"), file.FileContents);
    }

    /// <summary>Private Trips deny anonymous and foreign viewers before invoking either exporter.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Kml_ForbidForPrivateNonOwner(bool google, bool anonymous)
    {
        var db = CreateDbContext();
        db.Trips.Add(new Trip { Id = Guid.NewGuid(), UserId = "owner", Name = "Trip", IsPublic = false });
        await db.SaveChangesAsync();
        var exportSvc = new Mock<ITripExportService>(MockBehavior.Strict);
        var controller = BuildController(db, TestDataFixtures.CreateUser(id: "other"), exportSvc.Object);
        if (anonymous) controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());

        var result = google ? await controller.ExportGoogleMyMapsKml(db.Trips.Single().Id)
            : await controller.ExportWayfarerKml(db.Trips.Single().Id);

        Assert.IsType<ForbidResult>(result);
        exportSvc.VerifyNoOtherCalls();
    }

    /// <summary>Missing Trips remain 404 without reaching serialization.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Kml_NotFoundWhenMissing(bool google)
    {
        var controller = BuildController(CreateDbContext(), TestDataFixtures.CreateUser(),
            new Mock<ITripExportService>(MockBehavior.Strict).Object);
        var result = google ? await controller.ExportGoogleMyMapsKml(Guid.NewGuid())
            : await controller.ExportWayfarerKml(Guid.NewGuid());
        Assert.IsType<NotFoundResult>(result);
    }

    /// <summary>Serializer argument failures and cancellation propagate unchanged to existing request handling.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Kml_PropagatesExporterFailure(bool google, bool cancelled)
    {
        using var db = CreateDbContext();
        var trip = TestDataFixtures.CreateTrip("owner", isPublic: true);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        Exception failure = cancelled ? new OperationCanceledException() : new ArgumentException("private XML content");
        var service = new Mock<ITripExportService>();
        service.Setup(s => s.GenerateWayfarerKml(trip.Id)).Throws(failure);
        service.Setup(s => s.GenerateGoogleMyMapsKml(trip.Id)).Throws(failure);
        var controller = BuildController(db, TestDataFixtures.CreateUser(), service.Object);

        var observed = await Record.ExceptionAsync(() => google ? controller.ExportGoogleMyMapsKml(trip.Id)
            : controller.ExportWayfarerKml(trip.Id));

        Assert.Same(failure, observed);
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

    /// <summary>PDF preserves public/owner authorization and never invokes the renderer for another private viewer.</summary>
    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    [InlineData(false, "other", false)]
    [InlineData(false, "owner", true)]
    public async Task PdfAuthorization(bool isPublic, string? viewer, bool allowed)
    {
        using var db = CreateDbContext();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "owner", Name = "Trip", IsPublic = isPublic };
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        var service = new Mock<ITripExportService>();
        service.Setup(s => s.GeneratePdfGuideAsync(trip.Id, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream([1]));
        var controller = BuildController(db, TestDataFixtures.CreateUser(id: viewer ?? "anonymous"), service.Object);
        if (viewer == null) controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        var result = await controller.ExportPdf(trip.Id);
        if (allowed) Assert.IsType<FileStreamResult>(result);
        else Assert.IsType<ForbidResult>(result);
        service.Verify(s => s.GeneratePdfGuideAsync(trip.Id, null, It.IsAny<CancellationToken>()),
            allowed ? Times.Once() : Times.Never());
    }

    /// <summary>Saturation produces a bounded retry response and the service token observes request abortion.</summary>
    [Fact]
    public async Task PdfUnavailableHasRetryGuidanceAndRequestCancellation()
    {
        using var db = CreateDbContext();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "owner", Name = "Trip", IsPublic = true };
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        using var abort = new CancellationTokenSource();
        var service = new Mock<ITripExportService>();
        service.Setup(s => s.GeneratePdfGuideAsync(trip.Id, null, It.IsAny<CancellationToken>()))
            .Returns<Guid, string?, CancellationToken>((_, _, token) =>
            {
                abort.Cancel();
                Assert.True(token.IsCancellationRequested);
                throw new Wayfarer.Services.BrowserUnavailableException("busy");
            });
        var controller = BuildController(db, TestDataFixtures.CreateUser(), service.Object);
        controller.HttpContext.RequestAborted = abort.Token;
        var result = Assert.IsType<ObjectResult>(await controller.ExportPdf(trip.Id));
        Assert.Equal(503, result.StatusCode);
        Assert.Equal("10", controller.Response.Headers.RetryAfter);
    }

    /// <summary>Creates a controller with a synthetic authenticated viewer and request context.</summary>
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
