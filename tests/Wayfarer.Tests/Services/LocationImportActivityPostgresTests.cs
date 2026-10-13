using System.Data.Common;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationImports;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Exercises Activity reconciliation through the real worker and maintained PostgreSQL fixture.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationImportActivityPostgresTests(PostgresImportTestFixture fixture) : IAsyncLifetime
{
    private readonly string prefix = $"import-activity-{Guid.NewGuid():N}-";
    private readonly List<string> users = [];
    private readonly List<string> paths = [];

    /// <summary>Concurrent owners see the missing key before either insertion; both commit the winning FK.</summary>
    [PostgresFact]
    public async Task ConcurrentOwners_CreateOneActivityAndKeepCorrectForeignKeys()
    {
        var first = await SeedAsync([new(prefix + "ΟΣ")]);
        var second = await SeedAsync([new(prefix + "οσ")]);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        Task Barrier(CancellationToken token)
        {
            if (Interlocked.Increment(ref count) == 2) reached.TrySetResult();
            return reached.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        }
        var one = new OnceInterceptor(IsCatalogLookup, Barrier, after: true);
        var two = new OnceInterceptor(IsCatalogLookup, Barrier, after: true);

        var outcomes = await Task.WhenAll(
            Worker(one).ProcessImportExecution(first.Id, 0, CancellationToken.None),
            Worker(two).ProcessImportExecution(second.Id, 0, CancellationToken.None));

        Assert.All(outcomes, value => Assert.Equal(LocationImportExecutionOutcome.Completed, value));
        await using var db = fixture.CreateContext();
        var activity = Assert.Single(await db.ActivityTypes.Where(a => a.Name.StartsWith(prefix)).ToListAsync());
        Assert.Null(activity.Description);
        var locations = await db.Locations.Where(l => users.Contains(l.UserId)).ToListAsync();
        Assert.Equal(2, locations.Count);
        Assert.All(locations, location => Assert.Equal(activity.Id, location.ActivityTypeId));
    }

    /// <summary>Duplicate-only replay cannot create a novel label or fill an accepted null classification.</summary>
    [PostgresFact]
    public async Task DuplicateReplay_HasNoCatalogSideEffects()
    {
        var key = Guid.NewGuid();
        var first = await SeedAsync([new(null, key)]);
        await Worker().ProcessImport(first.Id, 0, CancellationToken.None);
        var replay = await SeedAsync([new(prefix + "novel", key)], first.UserId);
        await Worker().ProcessImport(replay.Id, 0, CancellationToken.None);

        await using var db = fixture.CreateContext();
        Assert.Null((await db.Locations.SingleAsync(l => l.UserId == first.UserId)).ActivityTypeId);
        Assert.False(await db.ActivityTypes.AnyAsync(a => a.Name.StartsWith(prefix)));
        var state = await db.LocationImports.SingleAsync(i => i.Id == replay.Id);
        Assert.Equal(ImportStatus.Completed, state.Status);
        Assert.Equal(1, state.SkippedDuplicates);
        Assert.Equal(0, state.UnclassifiedActivityCount);
    }

    /// <summary>A writer wins after precheck; its classification is retained and the losing novel name is never created.</summary>
    [PostgresFact]
    public async Task LateUuidLoser_CreatesNothingAndPreservesWinner()
    {
        var key = Guid.NewGuid();
        var import = await SeedAsync([new(prefix + "loser", key), new(prefix + "accepted")]);
        var winnerActivity = new ActivityType { Name = prefix + "winner", Description = "Retained prose" };
        await using (var seed = fixture.CreateContext())
        {
            seed.ActivityTypes.Add(winnerActivity);
            await seed.SaveChangesAsync();
        }
        var precheck = new OnceInterceptor(command => command.CommandText.Contains("IdempotencyKey", StringComparison.Ordinal)
            && command.CommandText.Contains("FROM \"Locations\"", StringComparison.Ordinal), async token =>
        {
            await using var external = fixture.CreateContext();
            external.Locations.Add(new Location
            {
                UserId = import.UserId, IdempotencyKey = key, ActivityTypeId = winnerActivity.Id,
                Coordinates = new(22, 40) { SRID = 4326 }, TimeZoneId = "UTC",
                Timestamp = DateTime.UnixEpoch, LocalTimestamp = DateTime.UnixEpoch, Source = "external-winner"
            });
            await external.SaveChangesAsync(token);
        }, after: true);

        await Worker(precheck).ProcessImport(import.Id, 0, CancellationToken.None);

        await using var db = fixture.CreateContext();
        var winner = await db.Locations.SingleAsync(l => l.UserId == import.UserId && l.IdempotencyKey == key);
        Assert.Equal(winnerActivity.Id, winner.ActivityTypeId);
        Assert.Equal("external-winner", winner.Source);
        Assert.False(await db.ActivityTypes.AnyAsync(a => a.Name == prefix + "loser"));
        Assert.Equal(2, await db.ActivityTypes.CountAsync(a => a.Name.StartsWith(prefix)));
        var state = await db.LocationImports.SingleAsync(i => i.Id == import.Id);
        Assert.Equal(ImportStatus.Completed, state.Status);
        Assert.Equal(1, state.SkippedDuplicates);
    }

    /// <summary>Admin-held rename/delete cannot commit a valid null FK, invalid count or checkpoint; restart uses current names.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdminContention_ExhaustsThreeAttemptsThenResumesAtomically(bool delete)
    {
        var import = await SeedAsync([new(new JValue(7)), new(prefix + "contended")]);
        await using var admin = fixture.CreateContext();
        var activity = new ActivityType { Name = prefix + "contended" };
        admin.ActivityTypes.Add(activity);
        await admin.SaveChangesAsync();
        await using var transaction = await admin.Database.BeginTransactionAsync();
        if (delete) admin.ActivityTypes.Remove(activity);
        else activity.Name += " renamed";
        await admin.SaveChangesAsync();
        var attempts = new CatalogAttemptCounter();

        await Worker(attempts).ProcessImport(import.Id, 0, CancellationToken.None);

        await using (var failed = fixture.CreateContext())
        {
            var state = await failed.LocationImports.SingleAsync(i => i.Id == import.Id);
            Assert.Equal(ImportStatus.Failed, state.Status);
            Assert.Equal(0, state.LastProcessedIndex);
            Assert.Equal(0, state.UnclassifiedActivityCount);
            Assert.Equal(0, state.SkippedDuplicates);
            Assert.Empty(await failed.Locations.Where(l => l.UserId == import.UserId).ToListAsync());
            Assert.Equal(3, attempts.Count);
            Assert.True(File.Exists(import.FilePath));
        }
        await transaction.CommitAsync();
        await RestartAsync(import.Id);
        await Worker().ProcessImport(import.Id, 1, CancellationToken.None);

        await using var db = fixture.CreateContext();
        var completed = await db.LocationImports.SingleAsync(i => i.Id == import.Id);
        Assert.Equal(ImportStatus.Completed, completed.Status);
        Assert.Equal(2, completed.LastProcessedIndex);
        Assert.Equal(1, completed.UnclassifiedActivityCount);
        var destination = await db.ActivityTypes.SingleAsync(a => a.Name == prefix + "contended");
        Assert.NotEqual(activity.Id, destination.Id);
        var locations = await db.Locations.Where(l => l.UserId == import.UserId).OrderBy(l => l.Timestamp).ToListAsync();
        Assert.Null(locations[0].ActivityTypeId);
        Assert.Equal(destination.Id, locations[1].ActivityTypeId);
    }

    /// <summary>A fresh second attempt succeeds when an Admin rename commits at its normalization boundary.</summary>
    [PostgresFact]
    public async Task ContentionClearsDuringRetry_CommitsOnlyTheFreshBatch()
    {
        var import = await SeedAsync([new(prefix + "retry")]);
        await using var admin = fixture.CreateContext();
        var original = new ActivityType { Name = prefix + "retry" };
        admin.ActivityTypes.Add(original);
        await admin.SaveChangesAsync();
        await using var transaction = await admin.Database.BeginTransactionAsync();
        original.Name += " renamed";
        await admin.SaveChangesAsync();
        var attempts = 0;
        var release = new OnceInterceptor(command => command.CommandText.Contains("unnest(", StringComparison.Ordinal)
            && Interlocked.Increment(ref attempts) == 2, token => transaction.CommitAsync(token));

        await Worker(release).ProcessImport(import.Id, 0, CancellationToken.None);

        await using var db = fixture.CreateContext();
        var destination = await db.ActivityTypes.SingleAsync(a => a.Name == prefix + "retry");
        Assert.NotEqual(original.Id, destination.Id);
        Assert.Equal(destination.Id, (await db.Locations.SingleAsync(l => l.UserId == import.UserId)).ActivityTypeId);
        var state = await db.LocationImports.SingleAsync(i => i.Id == import.Id);
        Assert.Equal(ImportStatus.Completed, state.Status);
        Assert.Equal(1, state.LastProcessedIndex);
        Assert.Equal(0, state.SkippedDuplicates);
        Assert.Equal(2, attempts);
    }

    /// <summary>The shared batch budget cancels a real PostgreSQL wait and reports failure, independently of user cancellation.</summary>
    [PostgresFact]
    public async Task BatchDeadline_RollsBackAndIsFailureRatherThanCancellation()
    {
        var import = await SeedAsync([new(prefix + "deadline")]);
        var wait = new OnceInterceptor(command => command.CommandText.Contains("unnest(", StringComparison.Ordinal), async token =>
        {
            await using var connection = fixture.CreateConnection();
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_sleep(45)";
            command.CommandTimeout = 60;
            await command.ExecuteNonQueryAsync(token);
        });

        var outcome = await Worker(wait).ProcessImportExecution(import.Id, 0, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(40));

        Assert.Equal(LocationImportExecutionOutcome.Failed, outcome);
        await using var db = fixture.CreateContext();
        Assert.Empty(await db.Locations.Where(l => l.UserId == import.UserId).ToListAsync());
        Assert.False(await db.ActivityTypes.AnyAsync(a => a.Name.StartsWith(prefix)));
        Assert.Equal(0, (await db.LocationImports.SingleAsync(i => i.Id == import.Id)).LastProcessedIndex);
    }

    /// <summary>Cancellation and epoch replacement after catalog insertion roll back all provisional effects.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostAuthorityAfterCatalogInsert_RollsBackBatch(bool cancel)
    {
        var import = await SeedAsync([new(prefix + "rollback")]);
        using var cancellation = new CancellationTokenSource();
        var boundary = new OnceInterceptor(command => command.CommandText.Contains("INSERT INTO \"ActivityTypes\"", StringComparison.Ordinal), async token =>
        {
            if (cancel) cancellation.Cancel();
            else
            {
                await using var authority = fixture.CreateContext();
                var current = await authority.LocationImports.SingleAsync(i => i.Id == import.Id, token);
                current.ExecutionEpoch++;
                await authority.SaveChangesAsync(token);
            }
        }, after: true);

        var outcome = await Worker(boundary).ProcessImportExecution(import.Id, 0, cancellation.Token);

        Assert.Equal(cancel ? LocationImportExecutionOutcome.Cancelled : LocationImportExecutionOutcome.Stale, outcome);
        await using var db = fixture.CreateContext();
        Assert.Empty(await db.Locations.Where(l => l.UserId == import.UserId).ToListAsync());
        Assert.False(await db.ActivityTypes.AnyAsync(a => a.Name.StartsWith(prefix)));
        Assert.Equal(0, (await db.LocationImports.SingleAsync(i => i.Id == import.Id)).LastProcessedIndex);
    }

    /// <summary>Actual B-tree capacity failure rolls back earlier catalog work and invalid facts, without contention retry.</summary>
    [PostgresFact]
    public async Task CatalogStorageFailure_IsFailureRatherThanInvalidInput()
    {
        await using var seed = fixture.CreateContext();
        var large = await seed.Database.SqlQueryRaw<string>("SELECT string_agg(md5(g::text), '') AS \"Value\" FROM generate_series(1,200) g").SingleAsync();
        var import = await SeedAsync([new(new JValue(7)), new(prefix + "a-valid"), new(prefix + "z-" + large)]);
        var attempts = new CatalogAttemptCounter();
        await Worker(attempts).ProcessImport(import.Id, 0, CancellationToken.None);

        await using var db = fixture.CreateContext();
        var state = await db.LocationImports.SingleAsync(i => i.Id == import.Id);
        Assert.Equal(ImportStatus.Failed, state.Status);
        Assert.Equal(0, state.LastProcessedIndex);
        Assert.Equal(0, state.UnclassifiedActivityCount);
        Assert.Empty(await db.Locations.Where(l => l.UserId == import.UserId).ToListAsync());
        Assert.False(await db.ActivityTypes.AnyAsync(a => a.Name.StartsWith(prefix)));
        Assert.Equal(1, attempts.Count);
    }

    /// <summary>A lost COMMIT acknowledgement is confirmed from a fresh checkpoint rather than replaying catalog creation.</summary>
    [PostgresFact]
    public async Task UnconfirmedCommit_ReadsDurableCheckpointAndCompletesOnce()
    {
        var import = await SeedAsync([new(prefix + "committed")]);
        var lost = new LostCommitAcknowledgement();
        await Worker(lost).ProcessImport(import.Id, 0, CancellationToken.None);

        await using var db = fixture.CreateContext();
        var state = await db.LocationImports.SingleAsync(i => i.Id == import.Id);
        Assert.Equal(ImportStatus.Completed, state.Status);
        Assert.Equal(1, state.LastProcessedIndex);
        Assert.Equal(0, state.SkippedDuplicates);
        Assert.Equal(1, lost.Commits);
        var activity = await db.ActivityTypes.SingleAsync(a => a.Name == prefix + "committed");
        Assert.Equal(activity.Id, (await db.Locations.SingleAsync(l => l.UserId == import.UserId)).ActivityTypeId);
    }

    /// <summary>The 50-row committed prefix and invalid count survive cancellation, reload and a new generation.</summary>
    [PostgresFact]
    public async Task Checkpoint51Rows_ResumesWithoutDoubleCountingOrRewriting()
    {
        var rows = Enumerable.Range(0, 51).Select(i => new Row(i is 0 or 50
            ? new JValue(42) : new JValue(prefix + "Walking"))).ToArray();
        var import = await SeedAsync(rows);
        using var cancellation = new CancellationTokenSource();
        var observer = new CheckpointObserver(cancellation);
        await Worker(observer: observer).ProcessImport(import.Id, 0, cancellation.Token);
        await using (var db = fixture.CreateContext())
        {
            var state = await db.LocationImports.SingleAsync(i => i.Id == import.Id);
            Assert.Equal(ImportStatus.Stopped, state.Status);
            Assert.Equal(50, state.LastProcessedIndex);
            Assert.Equal(1, state.UnclassifiedActivityCount);
            Assert.Equal(50, await db.Locations.CountAsync(l => l.UserId == import.UserId));
            state.Status = ImportStatus.InProgress;
            state.ExecutionEpoch++;
            await db.SaveChangesAsync();
        }

        await Worker().ProcessImport(import.Id, 1, CancellationToken.None);

        await using var verification = fixture.CreateContext();
        var completed = await verification.LocationImports.SingleAsync(i => i.Id == import.Id);
        Assert.Equal(ImportStatus.Completed, completed.Status);
        Assert.Equal(51, completed.LastProcessedIndex);
        Assert.Equal(2, completed.UnclassifiedActivityCount);
        Assert.Equal(0, completed.SkippedDuplicates);
        var locations = await verification.Locations.Where(l => l.UserId == import.UserId).ToListAsync();
        Assert.Equal(51, locations.Count);
        Assert.Equal(2, locations.Count(l => l.ActivityTypeId is null));
        var activity = Assert.Single(await verification.ActivityTypes.Where(a => a.Name.StartsWith(prefix)).ToListAsync());
        Assert.All(locations.Where(l => l.ActivityTypeId.HasValue), l => Assert.Equal(activity.Id, l.ActivityTypeId));
    }

    /// <summary>Names remain optional, Unicode-authoritative and unbounded by an invented creation allowance.</summary>
    [PostgresFact]
    public async Task NamesAndInvalidValues_AreDurableWithoutCreationQuotas()
    {
        var owner = await fixture.CreateUserAsync();
        users.Add(owner.Id);
        await using (var history = fixture.CreateContext())
        {
            var prior = Enumerable.Range(0, 257).Select(i => new ActivityType { Name = prefix + "prior-" + i }).ToArray();
            history.ActivityTypes.AddRange(prior);
            await history.SaveChangesAsync();
            history.Locations.AddRange(prior.Select((activity, i) => new Location
            {
                UserId = owner.Id, Coordinates = new(22, 40) { SRID = 4326 }, TimeZoneId = "UTC",
                Timestamp = DateTime.UnixEpoch.AddSeconds(i), LocalTimestamp = DateTime.UnixEpoch.AddSeconds(i),
                ActivityTypeId = activity.Id
            }));
            await history.SaveChangesAsync();
        }
        var values = Enumerable.Range(0, 65).Select(i => new Row(prefix + "new-" + i)).ToList();
        values.AddRange([
            new(" \u00a0" + prefix + "ΌΣ\u3000"), new(prefix + "όσ"),
            new(prefix + "one space"), new(prefix + "one  space"), new(prefix + "one\tspace"),
            new(prefix + "one\u00a0space"), new(prefix + new string('λ', 600)),
            new(null), new(" \t\u3000"), new(new JValue(7)), new(prefix + "\0bad"),
            new(prefix + "\u0001bad"), new(prefix + "\ud800bad")
        ]);
        var import = await SeedAsync(values, owner.Id);

        await Worker().ProcessImport(import.Id, 0, CancellationToken.None);

        await using var db = fixture.CreateContext();
        var state = await db.LocationImports.SingleAsync(i => i.Id == import.Id);
        Assert.Equal(ImportStatus.Completed, state.Status);
        Assert.Equal(values.Count, state.LastProcessedIndex);
        Assert.Equal(4, state.UnclassifiedActivityCount);
        var accepted = await db.Locations.Where(l => l.UserId == owner.Id && l.Timestamp >= new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)).OrderBy(l => l.Timestamp).ToListAsync();
        Assert.Equal(values.Count, accepted.Count);
        Assert.Equal(accepted[65].ActivityTypeId, accepted[66].ActivityTypeId);
        Assert.Equal(4, accepted.Skip(67).Take(4).Select(l => l.ActivityTypeId).Distinct().Count());
        Assert.All(accepted.Take(72), location => Assert.NotNull(location.ActivityTypeId));
        Assert.All(accepted.Skip(72), location => Assert.Null(location.ActivityTypeId));
        Assert.Equal(328, await db.ActivityTypes.CountAsync(a => a.Name.StartsWith(prefix)));
    }

    /// <summary>The actual owner route renders durable feedback on reload while preserving existing errors.</summary>
    [PostgresFact]
    public async Task OwnerHistory_ReloadShowsInvalidCountAndUploadExplainsSharedCatalog()
    {
        var import = await SeedAsync([new(new JValue(7)), new(null)]);
        await Worker().ProcessImport(import.Id, 0, CancellationToken.None);
        using var root = new TestDirectory();
        await using var db = fixture.CreateContext();
        var settings = await db.ApplicationSettings.AnyAsync() ? null : new ApplicationSettings();
        if (settings is not null) { db.ApplicationSettings.Add(settings); await db.SaveChangesAsync(); }
        try
        {
            var presentation = new Mock<ILocationEnrichmentPresentationProjector>();
            presentation.Setup(p => p.ProjectAsync(import.UserId, It.IsAny<CancellationToken>())).ReturnsAsync(
                LocationEnrichmentPresentation.Build(null,
                    new(null, "Not selected", false, "No provider selected.", false, 0, 0, "credits", "No usage window", null),
                    new(0, 0, 0, 0, null)));
            await using var app = await IdentityRouteHost.StartAsync(db, root.Path, services =>
            {
                services.AddSingleton(Moq.Mock.Of<Quartz.IScheduler>());
                services.AddSingleton(presentation.Object);
                services.AddSingleton(Moq.Mock.Of<ILocationImportLifecycle>());
            });
            using var client = app.GetTestClient();
            var scheme = IdentityConstants.ApplicationScheme;
            var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(scheme);
            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new(ClaimTypes.NameIdentifier, import.UserId), new(ClaimTypes.Role, "User")], scheme));
            var ticket = new AuthenticationTicket(principal, new AuthenticationProperties
                { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10) }, scheme);
            client.DefaultRequestHeaders.Add("Cookie", options.Cookie.Name + "=" + options.TicketDataFormat.Protect(ticket));
            using var response = await client.GetAsync("/User/LocationImport");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("Invalid Activity: 1 Location with unresolved Activity (invalid value).", html);
            Assert.Contains("Completed", html);
            using var upload = await client.GetAsync("/User/LocationImport/Upload");
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
            Assert.Contains("authenticated users", await upload.Content.ReadAsStringAsync());
        }
        finally
        {
            if (settings is not null) await db.ApplicationSettings.Where(s => s.Id == settings.Id).ExecuteDeleteAsync();
        }
    }

    /// <summary>Uses the existing durable owner restart semantics without reconstructing Quartz in each case.</summary>
    private async Task RestartAsync(int importId)
    {
        await using var db = fixture.CreateContext();
        var import = await db.LocationImports.SingleAsync(i => i.Id == importId);
        import.Status = ImportStatus.InProgress;
        import.ExecutionEpoch++;
        import.ErrorMessage = null;
        await db.SaveChangesAsync();
    }

    /// <summary>Creates a staged native input without a new parser or import harness.</summary>
    private async Task<LocationImport> SeedAsync(IEnumerable<Row> source, string? userId = null)
    {
        if (userId is null)
        {
            userId = (await fixture.CreateUserAsync()).Id;
            users.Add(userId);
        }
        var path = ImportStaging.TempFile();
        paths.Add(path);
        var features = source.Select((row, index) =>
        {
            var properties = new JObject
            {
                ["TimestampUtc"] = new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.FromHours(2)).AddSeconds(index * 2).ToString("O"),
                ["IdempotencyKey"] = (row.Key ?? Guid.NewGuid()).ToString("D"),
                ["ActivityTypeId"] = 999999 // Source numeric IDs never establish destination identity.
            };
            if (row.Activity is not null) properties["Activity"] = row.Activity;
            return new JObject
            {
                ["type"] = "Feature", ["geometry"] = new JObject { ["type"] = "Point", ["coordinates"] = new JArray(22, 40) },
                ["properties"] = properties
            };
        });
        var json = JsonConvert.SerializeObject(new JObject { ["type"] = "FeatureCollection", ["features"] = new JArray(features) },
            new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.EscapeNonAscii });
        await File.WriteAllTextAsync(path, json);
        var import = new LocationImport
        {
            UserId = userId, FilePath = path, FileType = LocationImportFileType.WayfarerGeoJson,
            Status = ImportStatus.InProgress, TotalRecords = 0, LastProcessedIndex = 0,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        await using var db = fixture.CreateContext();
        db.LocationImports.Add(import);
        await db.SaveChangesAsync();
        return import;
    }

    /// <summary>Attaches only the existing factory and observer seams.</summary>
    private LocationImportService Worker(IInterceptor? interceptor = null, ILocationImportLifecycleObserver? observer = null) =>
        new(ImportStaging.Files, new FixtureFactory(fixture, interceptor),
            new ReverseGeocodingService(new HttpClient(), NullLogger<BaseApiController>.Instance),
            NullLogger<LocationImportService>.Instance, new LocationDataParserFactory(NullLoggerFactory.Instance),
            new SseService(), null, observer ?? NullLocationImportLifecycleObserver.Instance);

    private static bool IsCatalogLookup(DbCommand command) => command.CommandText.Contains("FOR SHARE", StringComparison.Ordinal);

    /// <summary>Retains parsed scalar types and portable keys in test input.</summary>
    private sealed record Row(JToken? Activity, Guid? Key = null);

    private sealed class FixtureFactory(PostgresImportTestFixture fixture, IInterceptor? interceptor) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => interceptor is null ? fixture.CreateContext() : fixture.CreateContext(interceptor);
    }

    /// <summary>Counts real batch normalization attempts to bound contention and exclude storage-error retries.</summary>
    private sealed class CatalogAttemptCounter : DbCommandInterceptor
    {
        internal int Count { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("unnest(", StringComparison.Ordinal)) Count++;
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Injects loss only after PostgreSQL committed, at the maintained EF transaction observer seam.</summary>
    private sealed class LostCommitAcknowledgement : DbTransactionInterceptor
    {
        internal int Commits { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Commits++;
            throw new IOException("Commit acknowledgement unavailable.");
        }
    }

    /// <summary>Stops immediately after the durable checkpoint using the established lifecycle observer.</summary>
    private sealed class CheckpointObserver(CancellationTokenSource cancellation) : ILocationImportLifecycleObserver
    {
        public Task AfterBatchCommittedAsync(int importId, int epoch, int processed, CancellationToken token)
        {
            Assert.Equal(50, processed);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public Task BeforeTerminalPersistenceAsync(int importId, int epoch, LocationImportExecutionOutcome outcome, CancellationToken token) => Task.CompletedTask;
    }

    /// <summary>Runs one deterministic barrier/callback at an existing database command boundary.</summary>
    private sealed class OnceInterceptor(Func<DbCommand, bool> matches, Func<CancellationToken, Task> callback, bool after = false) : DbCommandInterceptor
    {
        private int invoked;
        private Task InvokeAsync(DbCommand command, CancellationToken token) => matches(command) && Interlocked.Exchange(ref invoked, 1) == 0
            ? callback(token) : Task.CompletedTask;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!after) await InvokeAsync(command, cancellationToken);
            return result;
        }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (after) await InvokeAsync(command, cancellationToken);
            return result;
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Removes only this test's rows and names, preserving unrelated persistent fixture data.</summary>
    public async Task DisposeAsync()
    {
        foreach (var path in paths) File.Delete(path);
        if (!fixture.IsAvailable) return;
        await using var db = fixture.CreateContext();
        await db.Locations.Where(l => users.Contains(l.UserId)).ExecuteDeleteAsync();
        await db.ActivityTypes.Where(a => a.Name.StartsWith(prefix)).ExecuteDeleteAsync();
    }
}
