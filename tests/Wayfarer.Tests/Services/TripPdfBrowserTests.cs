using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Exercises production PDF orchestration with controlled Playwright transport and real model loading.</summary>
[Collection(PlaywrightEnvironmentTestCollection.Name)]
public sealed class TripPdfBrowserTests : TestBase
{
    /// <summary>The 64-map limit leaves every textual region in the template and launches exactly one browser.</summary>
    [Fact]
    public async Task PdfReusesOneBrowserAndKeepsTextAfterMapBudget()
    {
        using var db = CreateDbContext();
        var trip = Seed(db, 70);
        var runtime = Runtime();
        var razor = new Mock<IRazorViewRenderer>();
        TripPrintViewModel? rendered = null;
        razor.Setup(r => r.RenderViewToStringAsync(It.IsAny<string>(), It.IsAny<object>()))
            .Callback<string, object>((_, model) => rendered = (TripPrintViewModel)model)
            .ReturnsAsync("<head></head><body>complete itinerary</body>");
        var service = Service(db, runtime.Playwright.Object, razor.Object);

        using var pdf = await service.GeneratePdfGuideAsync(trip.Id);

        Assert.Equal(70, rendered!.Regions.Count);
        Assert.Equal(64, rendered.Snap.Count);
        Assert.Contains("trip", rendered.Snap.Keys);
        Assert.DoesNotContain("region_" + rendered.Regions.Last().Id, rendered.Snap.Keys);
        runtime.Chromium.Verify(c => c.LaunchAsync(It.IsAny<BrowserTypeLaunchOptions>()), Times.Once);
        runtime.Browser.Verify(b => b.NewContextAsync(It.Is<BrowserNewContextOptions>(o =>
            o.ServiceWorkers == ServiceWorkerPolicy.Block && o.AcceptDownloads == false)), Times.Exactly(65));
        runtime.Page.Verify(p => p.EvaluateAsync<string>(It.IsAny<string>(), It.IsAny<object>()), Times.Exactly(64));
        runtime.Page.Verify(p => p.PdfAsync(It.IsAny<PagePdfOptions>()), Times.Once);
        runtime.Browser.Verify(b => b.CloseAsync(It.IsAny<BrowserCloseOptions>()), Times.Once);
        runtime.Playwright.Verify(p => p.Dispose(), Times.Once);
    }

    /// <summary>Both caller disconnect and the fixed server deadline interrupt an active navigation and release admission.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveCancellationClosesBrowserAndReturnsNoPartialPdf(bool serverDeadline)
    {
        using var db = CreateDbContext();
        var trip = Seed(db, 0);
        var runtime = Runtime();
        using var caller = new CancellationTokenSource();
        var clock = new DeadlineClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<IResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Page.Setup(p => p.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions>()))
            .Returns(() => { entered.SetResult(); return pending.Task; });
        runtime.Browser.Setup(b => b.CloseAsync(It.IsAny<BrowserCloseOptions>()))
            .Returns(() => { pending.TrySetException(new PlaywrightException("closed")); return Task.CompletedTask; });
        var service = Service(db, runtime.Playwright.Object, Mock.Of<IRazorViewRenderer>(), clock);
        var export = service.GeneratePdfGuideAsync(trip.Id, cancellationToken: caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (serverDeadline) clock.Expire(); else caller.Cancel();
        if (serverDeadline) await Assert.ThrowsAsync<BrowserUnavailableException>(() => export);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);
        runtime.Browser.Verify(b => b.CloseAsync(It.IsAny<BrowserCloseOptions>()), Times.Once);
        runtime.Playwright.Verify(p => p.Dispose(), Times.Once);
        runtime.Page.Verify(p => p.PdfAsync(It.IsAny<PagePdfOptions>()), Times.Never);
        using var first = BrowserAdmission.Shared.TryAcquire();
        using var second = BrowserAdmission.Shared.TryAcquire();
        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    /// <summary>Creates a public itinerary with stable display order and later text beyond the map budget.</summary>
    private static Trip Seed(ApplicationDbContext db, int regions)
    {
        var user = TestDataFixtures.CreateUser();
        var trip = new Trip { Id = Guid.NewGuid(), Name = "PDF boundary", UserId = user.Id, IsPublic = true };
        db.Users.Add(user);
        db.Trips.Add(trip);
        for (var i = 0; i < regions; i++) db.Regions.Add(new Region
        {
            Id = Guid.NewGuid(), TripId = trip.Id, UserId = user.Id, Name = "Region " + i,
            DisplayOrder = i, Center = new Point(23, 37) { SRID = 4326 }
        });
        db.SaveChanges();
        return trip;
    }

    /// <summary>Injects only browser creation and time; all PDF decisions remain production code.</summary>
    private static TripExportService Service(ApplicationDbContext db, IPlaywright runtime,
        IRazorViewRenderer razor, TimeProvider? clock = null)
    {
        var config = BrowserCaptureBoundaryTests.Configuration("wayfarer.example.org", "http://*:5500");
        return new TripExportService(db, new MapSnapshotService(NullLogger<MapSnapshotService>.Instance, config),
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, null!, razor,
            NullLogger<TripExportService>.Instance, config, new SseService(), Mock.Of<IImageProxyService>())
        { BrowserFactory = () => Task.FromResult(runtime), DeadlineClock = clock ?? TimeProvider.System };
    }

    /// <summary>Successful fixed-size maps and PDF output allow workflow counts to be verified without rendering fixtures.</summary>
    private static (Mock<IPlaywright> Playwright, Mock<IBrowserType> Chromium, Mock<IBrowser> Browser, Mock<IPage> Page) Runtime()
    {
        var runtime = new Mock<IPlaywright>();
        var chromium = new Mock<IBrowserType>();
        var browser = new Mock<IBrowser>();
        var context = new Mock<IBrowserContext>();
        var page = new Mock<IPage>();
        runtime.SetupGet(p => p.Chromium).Returns(chromium.Object);
        chromium.Setup(c => c.LaunchAsync(It.IsAny<BrowserTypeLaunchOptions>())).ReturnsAsync(browser.Object);
        browser.Setup(b => b.NewContextAsync(It.IsAny<BrowserNewContextOptions>())).ReturnsAsync(context.Object);
        context.Setup(c => c.NewPageAsync()).ReturnsAsync(page.Object);
        page.Setup(p => p.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions>())).ReturnsAsync((string url, PageGotoOptions _) =>
        {
            page.SetupGet(p => p.Url).Returns(url);
            return Mock.Of<IResponse>(r => r.Ok == true && r.Url == url);
        });
        page.Setup(p => p.EvaluateAsync<string>(It.IsAny<string>(), It.IsAny<object>())).ReturnsAsync("data:image/png;base64,AQI=");
        page.Setup(p => p.ScreenshotAsync(It.IsAny<PageScreenshotOptions>())).ReturnsAsync([1, 2]);
        page.Setup(p => p.PdfAsync(It.IsAny<PagePdfOptions>())).ReturnsAsync([3, 4]);
        return (runtime, chromium, browser, page);
    }

    /// <summary>Fires the server's actual timer only after active browser work begins.</summary>
    private sealed class DeadlineClock : TimeProvider
    {
        private Action? _expire;
        public void Expire() => _expire!();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromMinutes(5), dueTime);
            _expire = () => callback(state);
            return Mock.Of<ITimer>();
        }
    }
}
