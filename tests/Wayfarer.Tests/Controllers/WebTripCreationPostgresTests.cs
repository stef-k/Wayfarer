using System.Net;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Browser = Wayfarer.Tests.Controllers.ManagerAccountAuthorizationPostgresTests.Browser;

namespace Wayfarer.Tests.Controllers;

/// <summary>Proves atomic Web bootstrap, genuine form boundaries and audit safety against PostgreSQL.</summary>
public sealed class WebTripCreationPostgresTests(PostgresMigrationTestFixture fixture)
    : TestBase, IClassFixture<PostgresMigrationTestFixture>
{
    /// <summary>Both form redirects commit one server-owned fallback and ignore submitted child graphs.</summary>
    [PostgresTheory]
    [InlineData("save", "/User/Trip")]
    [InlineData("save-edit", "/User/Trip/Edit/")]
    public async Task FormCreationOwnsFallbackAndPreservesRedirects(string action, string redirect)
    {
        var id = Guid.NewGuid();
        var visibility = new CommitVisibility(fixture, id);
        await using var browser = await Browser.StartAsync(fixture, CreateTestDirectory(), "User", ConfigureWebServices, visibility);
        using var response = await browser.PostAsync("/User/Trip/Create",
            ("Id", id.ToString()), ("Name", "Web canonical"), ("submitAction", action),
            ("Notes", "<p onclick='bad()'>Safe</p><script>bad()</script>"), ("UserId", "untrusted"),
            ("Regions[0].Name", "Unassigned Places"), ("Regions[0].UserId", "untrusted"),
            ("Regions[1].Name", "Unassigned Places"), ("Regions[1].UserId", "untrusted"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(action == "save-edit" ? redirect + id : redirect, response.Headers.Location!.OriginalString);
        await using var db = fixture.CreateContext();
        var trip = await db.Trips.Include(item => item.Regions).SingleAsync(item => item.Id == id);
        Assert.Equal(1, visibility.BootstrapCommits);
        Assert.NotEqual("untrusted", trip.UserId);
        Assert.Equal("<p>Safe</p>", trip.Notes);
        var fallback = Assert.Single(trip.Regions);
        Assert.Equal(("Unassigned Places", 0, trip.UserId), (fallback.Name, fallback.DisplayOrder, fallback.UserId));
    }

    /// <summary>Real Region failure rolls back an already-inserted Trip; normal HandleError audit cannot resurrect either.</summary>
    [PostgresFact]
    public async Task RegionFailureThenNormalAuditCommitsNeitherTripNorRegion()
    {
        await using var browser = await Browser.StartAsync(fixture, CreateTestDirectory(), "User", ConfigureWebServices);
        var id = Guid.NewGuid();
        await using var db = fixture.CreateContext();
        var audits = await db.AuditLogs.Select(log => log.Id).ToArrayAsync();
        // nextval is not rolled back: a mistaken second graph save by LogAudit would succeed and expose the bug.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE SEQUENCE web_region_failure_once;
            CREATE FUNCTION reject_web_region_once() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF EXISTS (SELECT 1 FROM "Trips" WHERE "Id" = NEW."TripId" AND "Name" = 'Reject Web bootstrap')
                   AND nextval('web_region_failure_once') = 1 THEN
                    RAISE EXCEPTION 'Controlled Web Region insertion failure after Trip insertion' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER reject_web_region_once BEFORE INSERT ON "Regions"
                FOR EACH ROW EXECUTE FUNCTION reject_web_region_once();
            """);

        using var response = await browser.PostAsync("/User/Trip/Create",
            ("Id", id.ToString()), ("Name", "Reject Web bootstrap"), ("submitAction", "save"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await db.Trips.AnyAsync(trip => trip.Id == id));
        Assert.False(await db.Regions.AnyAsync(region => region.TripId == id));
        var audit = Assert.Single(await db.AuditLogs.Where(log => !audits.Contains(log.Id)).ToArrayAsync());
        Assert.Equal("Error", audit.Action);
        Assert.Contains("DbUpdateException", audit.Details);
    }

    /// <summary>Authentication, antiforgery and validation failures never create a Trip shell.</summary>
    [PostgresFact]
    public async Task RejectedFormsDoNotPersist()
    {
        await using var anonymous = await Browser.StartAsync(fixture, CreateTestDirectory(), "");
        var id = Guid.NewGuid();
        using var unauthenticated = await anonymous.PostAsync("/User/Trip/Create", ("Id", id.ToString()), ("Name", "Rejected"));
        Assert.Equal(HttpStatusCode.Redirect, unauthenticated.StatusCode);
        Assert.Contains("/Identity/Account/Login", unauthenticated.Headers.Location!.OriginalString);
        await using var browser = await Browser.StartAsync(fixture, CreateTestDirectory(), "User", ConfigureWebServices);
        using var noToken = await browser.PostAsync("/User/Trip/Create", false, ("Id", id.ToString()), ("Name", "Rejected"));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        using var invalid = await browser.PostAsync("/User/Trip/Create", ("Id", id.ToString()), ("Name", ""), ("submitAction", "save"));
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        await using var db = fixture.CreateContext();
        Assert.False(await db.Trips.AnyAsync(trip => trip.Id == id));
    }

    /// <summary>A cancelled save also abandons the graph before the normal synchronous error audit.</summary>
    [PostgresFact]
    public async Task CancelledCreationCannotBePersistedByAudit()
    {
        var user = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        using var cancellation = new CancellationTokenSource();
        db.SavingChanges += (_, _) => cancellation.Cancel();
        var controller = Controller(db, user.Id);
        controller.HttpContext.RequestAborted = cancellation.Token;
        var trip = new Trip { Id = Guid.NewGuid(), Name = "Cancelled Web bootstrap" };

        Assert.IsType<ViewResult>(await controller.Create(trip, "save"));

        await using var verify = fixture.CreateContext();
        Assert.False(await verify.Trips.AnyAsync(item => item.Id == trip.Id));
        Assert.False(await verify.Regions.AnyAsync(region => region.TripId == trip.Id));
        Assert.Contains(await verify.AuditLogs.ToArrayAsync(), log => log.Details.Contains("CanceledException"));
    }

    /// <summary>Failure auditing preserves the primary creation failure and cannot save the abandoned graph.</summary>
    [PostgresFact]
    public async Task AuditFailurePreservesBothFailuresWithoutResurrection()
    {
        var user = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        db.SavingChanges += (_, _) =>
        {
            if (db.ChangeTracker.Entries<Trip>().Any(entry => entry.State == EntityState.Added))
                throw new InvalidOperationException("Controlled creation failure");
            if (db.ChangeTracker.Entries<AuditLog>().Any(entry => entry.State == EntityState.Added))
                throw new InvalidOperationException("Controlled audit failure");
        };
        var controller = Controller(db, user.Id);
        var trip = new Trip { Id = Guid.NewGuid(), Name = "Failed audit" };

        var failure = await Assert.ThrowsAsync<AggregateException>(() => controller.Create(trip, "save"));

        Assert.Equal(new[] { "Controlled creation failure", "Controlled audit failure" },
            failure.InnerExceptions.Select(exception => exception.Message));
        Assert.Empty(db.ChangeTracker.Entries<Trip>());
        Assert.Empty(db.ChangeTracker.Entries<Region>());
        await using var verify = fixture.CreateContext();
        Assert.False(await verify.Trips.AnyAsync(item => item.Id == trip.Id));
    }

    /// <summary>A scheduler failure occurs after durable creation and preserves the success redirect with a warning.</summary>
    [PostgresFact]
    public async Task WarmupFailureKeepsCommittedGraphAndRedirect()
    {
        var user = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var scheduler = new Mock<ICacheWarmupScheduler>();
        var id = Guid.NewGuid();
        scheduler.Setup(service => service.ScheduleWarmupAsync(id, true)).Returns(async () =>
        {
            await using var verify = fixture.CreateContext();
            Assert.True(await verify.Trips.AnyAsync(trip => trip.Id == id));
            Assert.Equal(1, await verify.Regions.CountAsync(region => region.TripId == id));
            throw new InvalidOperationException("Controlled scheduler failure");
        });
        var controller = Controller(db, user.Id, scheduler.Object);

        var result = Assert.IsType<RedirectToActionResult>(await controller.Create(new Trip
        {
            Id = id, Name = "Committed Web bootstrap", CoverImageUrl = "https://example.test/cover.png"
        }, "save-edit"));

        Assert.Equal("Edit", result.ActionName);
        Assert.Equal("warning", controller.TempData["AlertType"]);
        scheduler.Verify(service => service.ScheduleWarmupAsync(id, true), Times.Once);
    }

    /// <summary>Replaces only external image work omitted by the established routed test host.</summary>
    private static void ConfigureWebServices(IServiceCollection services)
    {
        services.AddSingleton(Mock.Of<ICacheWarmupScheduler>());
        services.AddSingleton(Mock.Of<ITripMapThumbnailGenerator>());
    }

    /// <summary>Observes an independent reader while the bootstrap transaction is still uncommitted.</summary>
    private sealed class CommitVisibility(PostgresMigrationTestFixture fixture, Guid tripId) : DbTransactionInterceptor
    {
        public int BootstrapCommits { get; private set; }

        /// <summary>Neither row is visible until the one bootstrap transaction commits.</summary>
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Trip>().Any(entry => entry.Entity.Id == tripId))
            {
                BootstrapCommits++;
                await using var reader = fixture.CreateContext();
                Assert.False(await reader.Trips.AnyAsync(trip => trip.Id == tripId, cancellationToken));
                Assert.False(await reader.Regions.AnyAsync(region => region.TripId == tripId, cancellationToken));
            }
            return result;
        }
    }

    /// <summary>Builds the actual action and error-audit path with existing test identity/TempData conventions.</summary>
    private static TripController Controller(ApplicationDbContext db, string userId, ICacheWarmupScheduler? scheduler = null)
    {
        var context = BuildHttpContextWithUser(userId);
        return new TripController(NullLogger<TripController>.Instance, db,
            Mock.Of<ITripMapThumbnailGenerator>(), Mock.Of<ITripTagService>(), scheduler ?? Mock.Of<ICacheWarmupScheduler>())
        {
            ControllerContext = new() { HttpContext = context },
            TempData = new TempDataDictionary(context, Mock.Of<ITempDataProvider>())
        };
    }
}
