using System.Data.Common;
using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Wayfarer.Areas.Admin.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Manual consolidation at the existing PostgreSQL and routed Admin seams.</summary>
[Collection(PostgresMigrationTestCollection.Name)]
public sealed class ActivityConsolidationPostgresTests(PostgresMigrationTestFixture fixture) : TestBase
{
    /// <summary>Only Activity FKs change across owners; retirement, prose and import name identity remain intact.</summary>
    [PostgresFact]
    public async Task ConsolidationPreservesRowsAndFlattensRetiredIds()
    {
        var owner = await fixture.CreateUserAsync();
        var other = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        try
        {
            var (source, destination) = await SeedAsync(db);
            db.Locations.AddRange(ActivityCatalogMigrationPostgresTests.Row(owner.Id, source.Id),
                ActivityCatalogMigrationPostgresTests.Row(other.Id, source.Id),
                ActivityCatalogMigrationPostgresTests.Row(other.Id, destination.Id),
                ActivityCatalogMigrationPostgresTests.Row(owner.Id, null));
            await db.SaveChangesAsync();
            var original = await LocationBytesAsync(db);
            var catalog = new ActivityCatalog(db);
            var entries = await catalog.GetConsolidationEntriesAsync(source.Id, destination.Id);
            Assert.Equal(2, entries.Single(a => a.Id == source.Id).LocationCount);
            Assert.Equal(1, entries.Single(a => a.Id == destination.Id).LocationCount);

            Assert.True(await ConsolidateAsync(db, source.Id, destination.Id));

            Assert.Equal(original, await LocationBytesAsync(db));
            Assert.Equal(3, await db.Locations.CountAsync(l => l.ActivityTypeId == destination.Id));
            Assert.Equal(1, await db.Locations.CountAsync(l => l.ActivityTypeId == null));
            Assert.False(await db.ActivityTypes.AnyAsync(a => a.Id == source.Id));
            var retained = await db.ActivityTypes.AsNoTracking().SingleAsync();
            Assert.Equal(destination.Name, retained.Name);
            Assert.Equal($"Destination prose\n\nPreserved description from Activity {source.Id} ({source.Name}):\nSource prose", retained.Description);
            Assert.Equal(2, await db.Set<ActivityTypeIdRedirect>().CountAsync(r => r.ActivityTypeId == destination.Id));
            Assert.Equal(destination.Id, await catalog.ResolveIdAsync(source.Id));
            Assert.Equal(destination.Id, await catalog.ResolveWriteIdAsync(9000));
            var reuse = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"ActivityTypes\" (\"Id\", \"Name\") VALUES ({source.Id}, 'Reused ID')"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, reuse.SqlState);

            // Names are not aliases: the unchanged import reconciler can create the old name afresh.
            await using var transaction = await db.Database.BeginTransactionAsync();
            var imported = ActivityCatalogMigrationPostgresTests.Row(owner.Id, null);
            imported.ImportedActivityName = source.Name;
            Assert.Equal(0, await new ActivityCatalogReconciler(db).ReconcileAsync([imported], default));
            Assert.NotEqual(source.Id, imported.ActivityTypeId);
            Assert.NotEqual(destination.Id, imported.ActivityTypeId);
            db.Locations.Add(imported);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        finally { await ActivityCatalogMigrationPostgresTests.CleanAsync(db, owner.Id, other.Id); }
    }

    /// <summary>Exact repeats and Unicode whitespace follow the migration's deterministic prose policy.</summary>
    [PostgresTheory]
    [InlineData("same", "same", "same")]
    [InlineData("source", "\u0085\u00a0\u3000", "source")]
    [InlineData("\t\u202f", "destination", "destination")]
    [InlineData(null, null, null)]
    public async Task DescriptionPolicy(string? sourceText, string? destinationText, string? expected)
    {
        await using var db = fixture.CreateContext();
        try
        {
            var (source, destination) = await SeedAsync(db, sourceText, destinationText);
            Assert.True(await ConsolidateAsync(db, source.Id, destination.Id));
            Assert.Equal(expected, await db.ActivityTypes.Select(a => a.Description).SingleAsync());
        }
        finally { await ActivityCatalogMigrationPostgresTests.CleanAsync(db); }
    }

    /// <summary>Missing, identical, malformed and changed confirmations have no consolidation effects.</summary>
    [PostgresFact]
    public async Task InvalidAndStaleSelectionsAreRejected()
    {
        await using var db = fixture.CreateContext();
        try
        {
            var (source, destination) = await SeedAsync(db);
            var catalog = new ActivityCatalog(db);
            var entries = await catalog.GetConsolidationEntriesAsync(source.Id, destination.Id);
            var sourceVersion = entries.Single(a => a.Id == source.Id).Version;
            var destinationVersion = entries.Single(a => a.Id == destination.Id).Version;
            foreach (var (from, to, version) in new[]
            {
                (source.Id, source.Id, sourceVersion), (0, destination.Id, sourceVersion),
                (int.MaxValue, destination.Id, sourceVersion), (source.Id, int.MaxValue, sourceVersion),
                (source.Id, destination.Id, ""), (source.Id, destination.Id, "stale")
            })
                Assert.False(await catalog.ConsolidateAsync(from, to, version, destinationVersion));

            destination.Description = "New Admin prose";
            await db.SaveChangesAsync();
            Assert.False(await catalog.ConsolidateAsync(source.Id, destination.Id, sourceVersion, destinationVersion));
            Assert.Equal(2, await db.ActivityTypes.CountAsync());
            Assert.Equal(source.Id, (await db.Set<ActivityTypeIdRedirect>().SingleAsync()).ActivityTypeId);
            Assert.Equal("Source prose", await db.ActivityTypes.Where(a => a.Id == source.Id).Select(a => a.Description).SingleAsync());
            Assert.Equal("New Admin prose", await db.ActivityTypes.Where(a => a.Id == destination.Id).Select(a => a.Description).SingleAsync());
        }
        finally { await ActivityCatalogMigrationPostgresTests.CleanAsync(db); }
    }

    /// <summary>A late retirement failure restores the source, Locations, descriptions and older redirects.</summary>
    [PostgresFact]
    public async Task FailureAfterSourceRemovalRollsBackEverything()
    {
        var owner = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        try
        {
            var (source, destination) = await SeedAsync(db);
            db.Locations.Add(ActivityCatalogMigrationPostgresTests.Row(owner.Id, source.Id));
            await db.SaveChangesAsync();
            var boundary = new BeforeCommand("INSERT INTO \"ActivityTypeIdRedirects\"", () =>
                throw new InvalidOperationException("Injected failure after source removal"));
            await using var failing = fixture.CreateContext(boundary);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ConsolidateAsync(failing, source.Id, destination.Id));
            Assert.True(boundary.Reached);
            await AssertUnchangedAsync(db, source.Id, destination.Id, owner.Id);
        }
        finally { await ActivityCatalogMigrationPostgresTests.CleanAsync(db, owner.Id); }
    }

    /// <summary>A real Admin edit wins after reference moves; repeatable-read rejection preserves it and rolls back the merge.</summary>
    [PostgresFact]
    public async Task ConcurrentSourceEditIsPreservedAndConsolidationIsRejected()
    {
        var owner = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        try
        {
            var (source, destination) = await SeedAsync(db);
            db.Locations.Add(ActivityCatalogMigrationPostgresTests.Row(owner.Id, source.Id));
            await db.SaveChangesAsync();
            var boundary = new BeforeCommand("DELETE FROM \"ActivityTypes\"", async () =>
            {
                await using var editing = fixture.CreateContext();
                Assert.IsType<RedirectToActionResult>(await Admin(editing).Edit(source.Id,
                    new ActivityType { Id = source.Id, Name = source.Name, Description = "Concurrent Admin edit" }));
            });
            await using var merging = fixture.CreateContext(boundary);
            var entries = await new ActivityCatalog(merging).GetConsolidationEntriesAsync(source.Id, destination.Id);
            var controller = Admin(merging);
            Assert.IsType<RedirectToActionResult>(await controller.ConsolidateConfirmed(source.Id, destination.Id,
                entries.Single(a => a.Id == source.Id).Version, entries.Single(a => a.Id == destination.Id).Version));
            Assert.True(boundary.Reached);
            Assert.Contains("refresh", controller.TempData.Values.OfType<string>().Single(v => v.Contains("refresh")));
            await AssertUnchangedAsync(db, source.Id, destination.Id, owner.Id, "Concurrent Admin edit");
        }
        finally { await ActivityCatalogMigrationPostgresTests.CleanAsync(db, owner.Id); }
    }

    /// <summary>Production MVC renders an aggregate-only preview, enforces Admin/antiforgery and accepts its own confirmation.</summary>
    [PostgresFact]
    public async Task AdminRouteProtectsPreviewAndConfirmation()
    {
        var owner = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var settings = new ApplicationSettings();
        try
        {
            var (source, destination) = await SeedAsync(db);
            db.ApplicationSettings.Add(settings);
            var location = ActivityCatalogMigrationPostgresTests.Row(owner.Id, source.Id);
            location.Notes = "private-location-note";
            db.Locations.Add(location);
            await db.SaveChangesAsync();
            await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
            using var client = app.GetTestClient();
            var route = $"/Admin/ActivityType/Consolidate?sourceId={source.Id}&destinationId={destination.Id}";
            using var anonymous = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
            client.DefaultRequestHeaders.Add("Cookie", IdentityRouteHost.Cookie(app.Services, owner.Id, "User"));
            using var forbidden = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.Redirect, forbidden.StatusCode);
            Assert.Contains("AccessDenied", forbidden.Headers.Location!.ToString());
            using var forbiddenPost = await client.PostAsync("/Admin/ActivityType/Consolidate", new FormUrlEncodedContent([]));
            Assert.Equal(HttpStatusCode.Redirect, forbiddenPost.StatusCode);
            Assert.Contains("AccessDenied", forbiddenPost.Headers.Location!.ToString());
            client.DefaultRequestHeaders.Remove("Cookie");
            var auth = IdentityRouteHost.Cookie(app.Services, owner.Id, "Admin");
            client.DefaultRequestHeaders.Add("Cookie", auth);
            using var rejected = await client.PostAsync("/Admin/ActivityType/Consolidate", new FormUrlEncodedContent([]));
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            using var selection = await client.GetAsync($"/Admin/ActivityType/Consolidate?sourceId={source.Id}");
            Assert.Equal(HttpStatusCode.OK, selection.StatusCode);
            var options = await new HtmlParser().ParseDocumentAsync(await selection.Content.ReadAsStringAsync());
            Assert.NotNull(options.QuerySelector($"select[name='destinationId'] option[value='{destination.Id}']"));
            using var preview = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            var html = await preview.Content.ReadAsStringAsync();
            var document = await new HtmlParser().ParseDocumentAsync(html);
            var content = document.QuerySelector("main")!.TextContent;
            Assert.Contains(source.Name, content);
            Assert.Contains(destination.Name, content);
            Assert.Contains("1 Location", content);
            Assert.DoesNotContain(owner.Id, content);
            Assert.DoesNotContain("private-location-note", html);
            Assert.DoesNotContain(location.IdempotencyKey!.Value.ToString(), html);
            var form = document.QuerySelector("main form[method='post']")!;
            var fields = form.QuerySelectorAll("input[name]").ToDictionary(e => e.GetAttribute("name")!, e => e.GetAttribute("value")!);
            Assert.Equal(source.Id.ToString(), fields["sourceId"]);
            Assert.Equal(destination.Id.ToString(), fields["destinationId"]);
            client.DefaultRequestHeaders.Remove("Cookie");
            var csrfCookie = preview.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal)).Split(';')[0];
            client.DefaultRequestHeaders.Add("Cookie", auth + "; " + csrfCookie);
            using var confirmed = await client.PostAsync("/Admin/ActivityType/Consolidate", new FormUrlEncodedContent(fields));
            Assert.Equal(HttpStatusCode.Redirect, confirmed.StatusCode);
            Assert.False(await db.ActivityTypes.AnyAsync(a => a.Id == source.Id),
                $"Confirmation failed: {confirmed.Headers.Location}; fields: {string.Join(",", fields.Where(f => f.Key != "__RequestVerificationToken"))}");
            Assert.Equal(destination.Id, await db.Locations.Select(l => l.ActivityTypeId).SingleAsync());
        }
        finally
        {
            await db.ApplicationSettings.Where(s => s.Id == settings.Id).ExecuteDeleteAsync();
            await ActivityCatalogMigrationPostgresTests.CleanAsync(db, owner.Id);
        }
    }

    /// <summary>Seeds two live choices and an older ID that must be flattened.</summary>
    private static async Task<(ActivityType Source, ActivityType Destination)> SeedAsync(ApplicationDbContext db,
        string? sourceText = "Source prose", string? destinationText = "Destination prose")
    {
        var source = new ActivityType { Name = "Source Activity", Description = sourceText };
        var destination = new ActivityType { Name = "Destination Activity", Description = destinationText };
        db.ActivityTypes.AddRange(source, destination);
        await db.SaveChangesAsync();
        db.Set<ActivityTypeIdRedirect>().Add(new ActivityTypeIdRedirect { LegacyId = 9000, ActivityTypeId = source.Id });
        await db.SaveChangesAsync();
        return (source, destination);
    }

    /// <summary>Uses exactly the catalog versions that a fresh Admin confirmation would submit.</summary>
    private static async Task<bool> ConsolidateAsync(ApplicationDbContext db, int source, int destination)
    {
        var catalog = new ActivityCatalog(db);
        var entries = await catalog.GetConsolidationEntriesAsync(source, destination);
        return await catalog.ConsolidateAsync(source, destination,
            entries.Single(a => a.Id == source).Version, entries.Single(a => a.Id == destination).Version);
    }

    /// <summary>Compares every stored Location column except the single authorized FK change.</summary>
    private static Task<string> LocationBytesAsync(ApplicationDbContext db) => db.Database.SqlQueryRaw<string>("""
        SELECT jsonb_agg(to_jsonb(l) - 'ActivityTypeId' ORDER BY "Id")::text AS "Value" FROM "Locations" l
        """).SingleAsync();

    /// <summary>Reads committed state independently of the consolidation context's raw SQL writes.</summary>
    private static async Task AssertUnchangedAsync(ApplicationDbContext db, int source, int destination,
        string owner, string sourceText = "Source prose")
    {
        Assert.Equal(2, await db.ActivityTypes.CountAsync());
        Assert.Equal(source, await db.Locations.Where(l => l.UserId == owner).Select(l => l.ActivityTypeId).SingleAsync());
        Assert.Equal(source, (await db.Set<ActivityTypeIdRedirect>().AsNoTracking().SingleAsync()).ActivityTypeId);
        Assert.Equal(sourceText, await db.ActivityTypes.Where(a => a.Id == source).Select(a => a.Description).SingleAsync());
        Assert.Equal("Destination prose", await db.ActivityTypes.Where(a => a.Id == destination).Select(a => a.Description).SingleAsync());
    }

    /// <summary>Uses existing controller conventions for the focused concurrent Admin operation.</summary>
    private ActivityTypeController Admin(ApplicationDbContext db)
    {
        var context = BuildHttpContextWithUser("admin", "Admin");
        return new ActivityTypeController(NullLogger<ActivityTypeController>.Instance, db)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, Mock.Of<ITempDataProvider>())
        };
    }

    /// <summary>Runs one controlled failure or competing product operation at a real database command boundary.</summary>
    private sealed class BeforeCommand(string statement, Func<Task> action) : DbCommandInterceptor
    {
        /// <summary>Proves that the intended database boundary was exercised.</summary>
        public bool Reached { get; private set; }

        /// <summary>Runs once before the matching nonquery without replacing PostgreSQL execution.</summary>
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Reached && command.CommandText.Contains(statement, StringComparison.Ordinal))
            {
                Reached = true;
                await action();
            }
            return result;
        }
    }
}
