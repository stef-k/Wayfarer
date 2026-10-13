using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
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

/// <summary>Database normalization, Admin conflicts and restrictive EF/database deletion behavior.</summary>
[Collection(PostgresMigrationTestCollection.Name)]
public sealed class ActivityCatalogPostgresTests(PostgresMigrationTestFixture fixture)
{
    /// <summary>Real v1 vectors prove Unicode folding, NFC, trim-only and actual database enforcement.</summary>
    [PostgresFact]
    public async Task NormalizationAndDatabaseIdentity()
    {
        await using var db = fixture.CreateContext();
        var catalog = new ActivityCatalog(db);
        try
        {
            var equivalent = new[]
            {
                new[] { "Walking", " walking ", "WALKING", "wAlKiNg" },
                new[] { "Πεζοπορία", "ΠΕΖΟΠΟΡΙ\u0301Α", "\u00a0ΠΕΖΟΠΟΡΊΑ\u3000" },
                new[] { "ΟΣ", "ος", "οσ" }, new[] { "Straße", "STRASSE" }
            };
            foreach (var group in equivalent)
            {
                var expected = await catalog.NormalizeAsync(group[0]);
                foreach (var raw in group)
                    Assert.Equal(expected.NormalizedName, (await catalog.NormalizeAsync(raw)).NormalizedName);
            }
            var distinct = new[] { "Eating Out", "Eating  Out", "Eating\tOut", "Eating\u00a0Out", "Eating\rOut", "Eating\nOut" };
            var keys = new List<string?>();
            foreach (var raw in distinct)
            {
                var name = await catalog.NormalizeAsync(raw);
                Assert.Equal(raw, name.DisplayName);
                keys.Add(name.NormalizedName);
            }
            Assert.Equal(distinct.Length, keys.Distinct(StringComparer.Ordinal).Count());
            foreach (var pair in new[] { new[] { "Walking", "Hiking" }, new[] { "Α", "A" }, new[] { "i", "ı" }, new[] { "i", "İ" }, new[] { "ά", "α" } })
                Assert.NotEqual((await catalog.NormalizeAsync(pair[0])).NormalizedName, (await catalog.NormalizeAsync(pair[1])).NormalizedName);
            var whitespace = "\t\n\v\f\r \u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000";
            Assert.Equal("GPS", (await catalog.NormalizeAsync(whitespace + "GPS" + whitespace)).DisplayName);
            Assert.Equal("", (await catalog.NormalizeAsync(whitespace)).DisplayName);
            Assert.Null((await catalog.NormalizeAsync(null)).DisplayName);
            foreach (var invalid in new[] { "A\0B", "A\u0001B", "A\u000bB", "A\u000cB", "A\ufffeB", "A\uffffB", "\ud800" })
                Assert.Null((await catalog.NormalizeAsync(invalid)).NormalizedName);
            foreach (var valid in new[] { "GPS", "eBike", "संस्कृत\u200d", "👩‍🚀\ufe0f", "A\u200bB", new string('a', 101), new string('α', 700) })
            {
                Assert.Equal(valid, (await catalog.NormalizeAsync(valid)).DisplayName);
                db.ActivityTypes.Add(new ActivityType { Name = valid });
            }
            await catalog.SaveInitialAsync(); // Empty-schema seeding shares v1; generated keys remain private.
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"ActivityTypes\" (\"Name\") VALUES ('gps')"));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
            foreach (var invalid in new[] { " GPS ", "", "A\u0001B" })
                await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO \"ActivityTypes\" (\"Name\") VALUES ({invalid})"));
        }
        finally { await ActivityCatalogMigrationPostgresTests.CleanAsync(db); }
    }

    /// <summary>Explicit display/description edits keep IDs; references protect even tracked dependents.</summary>
    [PostgresFact]
    public async Task AdminValidationAndReferencedDeletion()
    {
        var owner = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        try
        {
            var created = new ActivityType { Name = " eBike ", Description = "Explicit prose" };
            Assert.IsType<RedirectToActionResult>(await Admin(db).Create(created));
            Assert.Equal("eBike", created.Name);
            var duplicate = Admin(db);
            Assert.IsType<ViewResult>(await duplicate.Create(new ActivityType { Name = "EBIKE" }));
            Assert.Contains("already exists", duplicate.ModelState["Name"]!.Errors.Single().ErrorMessage);
            Assert.IsType<RedirectToActionResult>(await Admin(db).Edit(created.Id,
                new ActivityType { Id = created.Id, Name = "EBike", Description = "Changed explicitly" }));
            var other = new ActivityType { Name = "GPS" };
            db.ActivityTypes.Add(other);
            await db.SaveChangesAsync();
            Assert.IsType<ViewResult>(await Admin(db).Edit(other.Id, new ActivityType { Id = other.Id, Name = "ebike" }));
            db.ChangeTracker.Clear();
            var invalid = Admin(db);
            Assert.IsType<ViewResult>(await invalid.Create(new ActivityType { Name = " \t" }));
            Assert.False(invalid.ModelState.IsValid);
            db.Locations.Add(ActivityCatalogMigrationPostgresTests.Row(owner.Id, created.Id));
            db.Set<ActivityTypeIdRedirect>().Add(new ActivityTypeIdRedirect { LegacyId = 9000, ActivityTypeId = created.Id });
            await db.SaveChangesAsync();
            var tracked = await db.Locations.Include(l => l.ActivityType).SingleAsync();
            Assert.IsType<RedirectToActionResult>(await Admin(db).DeleteConfirmed(created.Id));
            Assert.Equal(created.Id, tracked.ActivityTypeId);
            var target = tracked.ActivityType!;
            db.ActivityTypes.Remove(target); // Exercise ClientNoAction as well as the database FK.
            Assert.Equal(created.Id, tracked.ActivityTypeId);
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ((PostgresException)failure.InnerException!).SqlState);
            db.ChangeTracker.Clear();
            await db.Locations.ExecuteDeleteAsync();
            Assert.IsType<RedirectToActionResult>(await Admin(db).DeleteConfirmed(created.Id));
            Assert.True(await db.ActivityTypes.AnyAsync(a => a.Id == created.Id)); // Redirect alone protects survivor.
            Assert.IsType<RedirectToActionResult>(await Admin(db).DeleteConfirmed(other.Id));
            Assert.False(await db.ActivityTypes.AnyAsync(a => a.Id == other.Id));
        }
        finally { await ActivityCatalogMigrationPostgresTests.CleanAsync(db, owner.Id); }
    }

    /// <summary>Concurrent create and rename prechecks both pass; the unique index supplies useful feedback.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdminUniquenessRace(bool rename)
    {
        await using var cleanup = fixture.CreateContext();
        try
        {
            var first = new ActivityType { Name = rename ? "First" : " Racing " };
            var second = new ActivityType { Name = rename ? "Second" : "RACING" };
            if (rename)
            {
                cleanup.ActivityTypes.AddRange(first, second);
                await cleanup.SaveChangesAsync();
                first.Name = " Racing "; second.Name = "RACING";
            }
            var barrier = new SaveBarrier();
            await using var one = fixture.CreateContext(barrier);
            await using var two = fixture.CreateContext(barrier);
            var c1 = Admin(one); var c2 = Admin(two);
            var results = rename
                ? await Task.WhenAll(c1.Edit(first.Id, first), c2.Edit(second.Id, second))
                : await Task.WhenAll(c1.Create(first), c2.Create(second));
            Assert.Single(results.OfType<RedirectToActionResult>());
            Assert.Single(results.OfType<ViewResult>());
            Assert.Single(new[] { c1, c2 }, c => !c.ModelState.IsValid);
            Assert.Equal(1, await cleanup.ActivityTypes.CountAsync(a => EF.Property<string>(a, "NormalizedName") == "racing"));
        }
        finally { await ActivityCatalogMigrationPostgresTests.CleanAsync(cleanup); }
    }

    /// <summary>Commits a new Location after delete prechecks; the controller must translate the FK race.</summary>
    [PostgresFact]
    public async Task DeleteRacePreservesTheNewReference()
    {
        var owner = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var activity = new ActivityType { Name = "Race delete" };
        db.ActivityTypes.Add(activity);
        await db.SaveChangesAsync();
        try
        {
            var interceptor = new BeforeActivitySave(async () =>
            {
                await using var writer = fixture.CreateContext();
                writer.Locations.Add(ActivityCatalogMigrationPostgresTests.Row(owner.Id, activity.Id));
                await writer.SaveChangesAsync();
            });
            await using var deleting = fixture.CreateContext(interceptor);
            Assert.IsType<RedirectToActionResult>(await Admin(deleting).DeleteConfirmed(activity.Id));
            Assert.Equal(activity.Id, await db.Locations.Select(l => l.ActivityTypeId).SingleAsync());
            Assert.True(await db.ActivityTypes.AnyAsync(a => a.Id == activity.Id));
        }
        finally { await ActivityCatalogMigrationPostgresTests.CleanAsync(db, owner.Id); }
    }

    /// <summary>Both PostgreSQL index-size limits return the submitted form without a pending failed write.</summary>
    [PostgresTheory]
    [InlineData(false, 200)]
    [InlineData(true, 200)]
    [InlineData(false, 400)]
    [InlineData(true, 400)]
    public async Task AdminCapacityFailureReturnsNameValidation(bool edit, int chunks)
    {
        await using var db = fixture.CreateContext();
        var original = new ActivityType { Name = "Original", Description = "Unchanged" };
        var unrelated = new ActivityType { Name = "Unrelated", Description = "Untouched" };
        try
        {
            db.ActivityTypes.Add(unrelated);
            if (edit) db.ActivityTypes.Add(original);
            await db.SaveChangesAsync();
            var longName = await db.Database.SqlQuery<string>($"""
                SELECT string_agg(md5(g::text), '') AS "Value" FROM generate_series(1, {chunks}) g
                """).SingleAsync();
            var submitted = new ActivityType { Id = original.Id, Name = longName, Description = "Submitted prose" };
            var controller = Admin(db);
            var result = edit ? await controller.Edit(original.Id, submitted) : await controller.Create(submitted);

            var view = Assert.IsType<ViewResult>(result);
            Assert.Same(submitted, view.Model);
            Assert.Equal(longName, submitted.Name);
            Assert.Equal("Submitted prose", submitted.Description);
            Assert.NotEmpty(controller.ModelState[nameof(ActivityType.Name)]!.Errors);
            Assert.DoesNotContain(db.ChangeTracker.Entries<ActivityType>(), e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
            await db.SaveChangesAsync(); // A later save must not retry the rejected mutation.

            await using var verify = fixture.CreateContext();
            Assert.Equal(edit ? 2 : 1, await verify.ActivityTypes.CountAsync());
            var retained = await verify.ActivityTypes.SingleAsync(a => a.Id == unrelated.Id);
            Assert.Equal("Unrelated", retained.Name);
            Assert.Equal("Untouched", retained.Description);
            if (edit)
            {
                var unchanged = await verify.ActivityTypes.SingleAsync(a => a.Id == original.Id);
                Assert.Equal("Original", unchanged.Name);
                Assert.Equal("Unchanged", unchanged.Description);
            }
        }
        finally { await ActivityCatalogMigrationPostgresTests.CleanAsync(db); }
    }

    /// <summary>A committed winning delete produces the established response and cannot resurrect stale state.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentDeletionReturnsHandledResponse(bool edit)
    {
        await using var setup = fixture.CreateContext();
        var activity = new ActivityType { Name = "Deletion race", Description = "Original" };
        var unrelated = new ActivityType { Name = "Unrelated", Description = "Untouched" };
        try
        {
            setup.ActivityTypes.AddRange(activity, unrelated);
            await setup.SaveChangesAsync();
            var barrier = new BeforeActivitySave(async () =>
            {
                await using var winner = fixture.CreateContext();
                Assert.Equal(1, await winner.ActivityTypes.Where(a => a.Id == activity.Id).ExecuteDeleteAsync());
            });
            await using var losing = fixture.CreateContext(barrier);
            var controller = Admin(losing);
            var result = edit
                ? await controller.Edit(activity.Id, new ActivityType
                    { Id = activity.Id, Name = "Renamed", Description = "Submitted prose" })
                : await controller.DeleteConfirmed(activity.Id);

            if (edit) Assert.IsType<NotFoundResult>(result);
            else Assert.Equal(nameof(ActivityTypeController.Index), Assert.IsType<RedirectToActionResult>(result).ActionName);
            Assert.Empty(losing.ChangeTracker.Entries<ActivityType>());
            await losing.SaveChangesAsync();
            await using var verify = fixture.CreateContext();
            Assert.False(await verify.ActivityTypes.AnyAsync(a => a.Id == activity.Id));
            var retained = Assert.Single(await verify.ActivityTypes.ToListAsync());
            Assert.Equal(unrelated.Id, retained.Id);
            Assert.Equal("Unrelated", retained.Name);
            Assert.Equal("Untouched", retained.Description);
            Assert.Empty(await verify.AuditLogs.ToListAsync());
        }
        finally { await ActivityCatalogMigrationPostgresTests.CleanAsync(setup); }
    }

    /// <summary>An unrelated 54000 is a failed operation, not Name validation or a retried catalog mutation.</summary>
    [PostgresTheory]
    [InlineData("create")]
    [InlineData("edit")]
    [InlineData("delete")]
    public async Task UnexpectedPersistenceFailureDoesNotRetryActivity(string operation)
    {
        await using var db = fixture.CreateContext();
        var original = new ActivityType { Name = "Original", Description = "Unchanged" };
        try
        {
            db.ActivityTypes.Add(original);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION public.activity_test_fail() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION USING ERRCODE = '54000', MESSAGE = 'unrelated program limit'; END $$;
                CREATE TRIGGER activity_test_fail BEFORE INSERT OR UPDATE OR DELETE ON "ActivityTypes"
                    FOR EACH ROW EXECUTE FUNCTION public.activity_test_fail();
                """);
            var submitted = new ActivityType
                { Id = operation == "edit" ? original.Id : 0, Name = "Proposed", Description = "Submitted prose" };
            var controller = Admin(db);
            var result = operation switch
            {
                "create" => await controller.Create(submitted),
                "edit" => await controller.Edit(original.Id, submitted),
                _ => await controller.DeleteConfirmed(original.Id)
            };

            if (operation == "delete") Assert.IsType<RedirectToActionResult>(result);
            else Assert.Same(submitted, Assert.IsType<ViewResult>(result).Model);
            Assert.True(controller.ModelState.IsValid);
            Assert.Equal("danger", controller.TempData["AlertType"]);
            Assert.DoesNotContain(db.ChangeTracker.Entries<ActivityType>(), e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
            await db.SaveChangesAsync();
            await using var verify = fixture.CreateContext();
            var retained = Assert.Single(await verify.ActivityTypes.ToListAsync());
            Assert.Equal(original.Id, retained.Id);
            Assert.Equal("Original", retained.Name);
            Assert.Equal("Unchanged", retained.Description);
            Assert.Empty(await verify.AuditLogs.ToListAsync());
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS activity_test_fail ON \"ActivityTypes\"; DROP FUNCTION IF EXISTS public.activity_test_fail()");
            await ActivityCatalogMigrationPostgresTests.CleanAsync(db);
        }
    }

    /// <summary>Existing Admin context and TempData convention, without external work.</summary>
    internal static ActivityTypeController Admin(ApplicationDbContext db)
    {
        var context = new DefaultHttpContext();
        return new ActivityTypeController(NullLogger<ActivityTypeController>.Instance, db)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, Mock.Of<ITempDataProvider>())
        };
    }

    /// <summary>Both writers pass product prechecks before either attempts the real database write.</summary>
    private sealed class SaveBarrier : SaveChangesInterceptor
    {
        private int arrived;
        private readonly TaskCompletionSource both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <inheritdoc />
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref arrived) == 2) both.TrySetResult();
            await both.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
    }

    /// <summary>Commits the competing operation after product reads and before the pending Activity write.</summary>
    private sealed class BeforeActivitySave(Func<Task> competingWrite) : SaveChangesInterceptor
    {
        /// <inheritdoc />
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<ActivityType>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
                await competingWrite();
            return result;
        }
    }
}
