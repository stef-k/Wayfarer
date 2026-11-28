using System.Data.Common;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Services.TripMetadataReceipts;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;
using static Wayfarer.Tests.Controllers.TripMetadataReceiptHttp;

namespace Wayfarer.Tests.Controllers;

/// <summary>Qualifies retirement and restore only in the guarded disposable PostgreSQL database.</summary>
public sealed class TripMetadataReceiptRetirementPostgresTests(PostgresMigrationTestFixture fixture) : TestBase, IClassFixture<PostgresMigrationTestFixture>
{
    private readonly Mock<ICacheWarmupScheduler> _warmup = new();

    /// <summary>Checks the deadline after an actual Trip lock wait and distinguishes elapsed from durably retired.</summary>
    [PostgresFact]
    public async Task WaitingWriterCannotPassDeadline_AndClockRollbackCannotReopenRetiredWork()
    {
        var trip = await SeedAsync();
        var clock = new DatabaseClock();
        var gate = new WriteGate();
        await using var db = fixture.CreateContext(clock);
        await using var app = await StartAsync(db, clock, gate);
        using var client = Client(app, trip);
        var body = Body(new { name = trip.Name }, new { name = "Must not execute late" });
        var permit = await PermitAsync(client, trip, body);
        var deadline = permit.GetProperty("executeBeforeUtc").GetDateTime();
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Trips\" WHERE \"Id\" = {trip.Id} FOR UPDATE");
        var pending = SendAsync(client, trip, permit, body);
        try
        {
            var pid = await gate.TripLock.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForBlockAsync(blocker, pid);
            clock.ShiftSeconds = 16 * 60;
        }
        finally { await transaction.RollbackAsync(); }
        using var late = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        using var elapsed = await LookupAsync(client, trip, permit);
        await EvidenceAsync(elapsed, permit, "unresolved");
        var store = new TripMetadataReceiptStore(db);
        await store.AdvanceCutoffAsync(default);
        clock.ShiftSeconds = -7200;
        using var retired = await LookupAsync(client, trip, permit);
        Assert.False((await EvidenceAsync(retired, permit, "retired")).GetProperty("futureEffects").GetBoolean());
        using var behind = await IssueAsync(client, trip, body);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, behind.StatusCode);
        Assert.DoesNotContain("operationId", await behind.Content.ReadAsStringAsync());
        using var replay = await SendAsync(client, trip, permit, body);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal(trip.Name, (await db.Trips.AsNoTracking().SingleAsync(row => row.Id == trip.Id)).Name);
        Assert.Empty(_warmup.Invocations);

        clock.ShiftSeconds = 17 * 60;
        var before = (await store.AdmissionAsync(default)).Now;
        var next = await PermitAsync(client, trip, body);
        var after = (await store.AdmissionAsync(default)).Now;
        Assert.InRange(next.GetProperty("executeBeforeUtc").GetDateTime(), before.AddMinutes(15), after.AddMinutes(15));
        Assert.True(deadline < before);
    }

    /// <summary>Cutoff and cleanup cannot invent a result while an admitted writer owns the fence.</summary>
    [PostgresFact]
    public async Task AdmittedWriterHoldsFence_AndCleanupAndRotationDoNotReopenIt()
    {
        var trip = await SeedAsync();
        var clock = new DatabaseClock();
        var gate = new WriteGate { PauseUpdate = true };
        var root = CreateTestDirectory();
        await using var db = fixture.CreateContext(clock);
        await using var app = await StartAsync(db, clock, gate, root);
        using var client = Client(app, trip);
        var body = Body(new { notes = trip.Notes }, new { notes = "Admitted before deadline" });
        var permit = await PermitAsync(client, trip, body);
        var store = new TripMetadataReceiptStore(db);
        var pending = SendAsync(client, trip, permit, body);
        try
        {
            await gate.Update.Task.WaitAsync(TimeSpan.FromSeconds(10));
            clock.ShiftSeconds = 16 * 60;
            await store.AdvanceCutoffAsync(default);
            using var inFlight = await LookupAsync(client, trip, permit);
            Assert.Equal(JsonValueKind.Null, (await EvidenceAsync(inFlight, permit, "unresolved")).GetProperty("futureEffects").ValueKind);
        }
        finally { gate.Resume.TrySetResult(); }
        using var applied = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        await EvidenceAsync(applied, permit, "applied");
        using var stored = await LookupAsync(client, trip, permit);
        await EvidenceAsync(stored, permit, "applied");
        clock.ShiftSeconds = 29 * 86400;
        await store.AdvanceCutoffAsync(default);
        await store.CleanupAsync(default);
        using var retained = await LookupAsync(client, trip, permit);
        await EvidenceAsync(retained, permit, "applied");
        clock.ShiftSeconds = 31 * 86400;
        await store.AdvanceCutoffAsync(default);
        await store.CleanupAsync(default);

        var manager = app.Services.GetRequiredService<IKeyManager>();
        manager.CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
        // A restarted node uses the same persistent ring and reads the new active key.
        await using var restarted = await StartAsync(db, clock, new WriteGate(), root);
        using var rotated = Client(restarted, trip);
        var renewed = await PermitAsync(rotated, trip, body);
        Assert.NotEqual(KeyId(permit), KeyId(renewed));
        clock.ShiftSeconds++;
        using var retired = await LookupAsync(rotated, trip, permit);
        await EvidenceAsync(retired, permit, "retired");
        clock.ShiftSeconds = 0;
        using var replay = await SendAsync(rotated, trip, permit, body);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal("Admitted before deadline", (await db.Trips.AsNoTracking().SingleAsync(row => row.Id == trip.Id)).Notes);
        Assert.Single(_warmup.Invocations);
    }

    /// <summary>Runs the managed activation SQL against earlier state with the same key ring before allowing the routed writer.</summary>
    [PostgresFact]
    public async Task RestoreActivationRefusesTimeEligiblePriorAuthorityAndReusesItsRecordedId()
    {
        using var directory = new TestDirectory();
        var trip = await SeedAsync();
        var clock = new DatabaseClock();
        await using var sourceDb = fixture.CreateContext(clock);
        await using var source = await StartAsync(sourceDb, clock, new WriteGate(), directory.Path);
        using var original = Client(source, trip);
        var body = Body(new { name = trip.Name }, new { name = "Old authority must not execute" });
        var permit = await PermitAsync(original, trip, body);
        var candidate = new PostgresMigrationTestFixture();
        try
        {
            await candidate.InitializeAsync();
            await CopyEarlierCandidateAsync(candidate, sourceDb, trip);
            using var first = await SendAsync(original, trip, permit, body);
            await EvidenceAsync(first, permit, "applied");
            await source.StopAsync();
            await VerifyActivatedCandidateAsync(candidate, clock, directory.Path, trip, permit, body);
        }
        finally { await candidate.DisposeAsync(); }
    }

    /// <summary>Creates a separate earlier database copy with the original route, owner, credential and proof authority.</summary>
    private static async Task CopyEarlierCandidateAsync(PostgresMigrationTestFixture candidate, ApplicationDbContext source, Trip trip)
    {
        await using var copy = candidate.CreateContext();
        var snapshot = await new TripMetadataReceiptStore(source).AdmissionAsync(default);
        copy.Users.Add(await source.Users.AsNoTracking().SingleAsync(row => row.Id == trip.UserId));
        copy.Trips.Add(await source.Trips.AsNoTracking().SingleAsync(row => row.Id == trip.Id));
        // A bearer token uses its primary-key-generated model ID. Copy the exact authorizing token value.
        copy.ApiTokens.Add(new ApiToken { UserId = trip.UserId, User = null!, Name = "Restored credential", TokenHash = ApiTokenService.HashToken(trip.UserId) });
        await copy.SaveChangesAsync();
        await copy.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.trip_metadata_proof_authority SET authority_id = {snapshot.Authority}, retired_through = {snapshot.Cutoff}");
    }

    /// <summary>Runs durable restore activation while writers are stopped and then opens the same persistent ring on the candidate.</summary>
    private async Task VerifyActivatedCandidateAsync(PostgresMigrationTestFixture candidate, DatabaseClock clock, string root, Trip trip,
        JsonElement permit, string body)
    {
        var restoreId = Guid.NewGuid();
        await ApplyActivationAsync(candidate, restoreId, true);
        await using var db = candidate.CreateContext(clock);
        var store = new TripMetadataReceiptStore(db);
        var activated = await store.AdmissionAsync(default);
        Assert.Equal(restoreId, activated.Authority);
        Assert.True(activated.Now < permit.GetProperty("executeBeforeUtc").GetDateTime());
        await ApplyActivationAsync(candidate, restoreId, true);
        await ApplyActivationAsync(candidate, restoreId, false);
        Assert.Equal(activated.Cutoff, (await store.AdmissionAsync(default)).Cutoff);

        await using var app = await StartAsync(db, clock, new WriteGate(), root, candidate);
        using var client = Client(app, trip);
        using var replay = await SendAsync(client, trip, permit, body);
        using var lookup = await LookupAsync(client, trip, permit);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, lookup.StatusCode);
        Assert.DoesNotContain("historicalOutcome", await lookup.Content.ReadAsStringAsync());
        Assert.Equal(trip.Name, (await db.Trips.AsNoTracking().SingleAsync(row => row.Id == trip.Id)).Name);
        using var fresh = await SendAsync(client, trip, await PermitAsync(client, trip, body), body);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    /// <summary>Executes the activation owner's SQL against only the guarded candidate database.</summary>
    private static async Task ApplyActivationAsync(PostgresMigrationTestFixture candidate, Guid operation, bool rotate)
    {
        if (!OperatingSystem.IsLinux()) throw Xunit.Sdk.SkipException.ForSkip("Managed restore qualification requires Linux.");
        await using var connection = new NpgsqlConnection(candidate.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(WayfarerCtl.RestoreTripProofAuthority.BuildSql(operation, rotate), connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Creates a private owner and an incoming credential within the isolated candidate.</summary>
    private async Task<Trip> SeedAsync()
    {
        var user = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        // Each test owns a separate continuity interval within the class's disposable database.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.trip_metadata_proof_authority SET authority_id = {Guid.NewGuid()}, retired_through = clock_timestamp()");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Earlier name", Notes = "Earlier notes" };
        db.Trips.Add(trip);
        db.ApiTokens.Add(new ApiToken { UserId = user.Id, User = null!, Name = "Receipt qualification", TokenHash = ApiTokenService.HashToken(user.Id) });
        await db.SaveChangesAsync();
        await db.Entry(trip).ReloadAsync();
        return trip;
    }

    /// <summary>Starts the same routed Identity/PostgreSQL owner with test-only timing gates.</summary>
    private Task<WebApplication> StartAsync(ApplicationDbContext db, DatabaseClock clock, WriteGate gate, string? root = null,
        PostgresMigrationTestFixture? target = null) =>
        IdentityRouteHost.StartAsync(db, root ?? CreateTestDirectory(), services =>
        {
            services.AddScoped(_ => (target ?? fixture).CreateContext(clock, gate));
            services.AddSingleton(_warmup.Object);
            services.AddSingleton(Mock.Of<ITripMapThumbnailGenerator>());
            services.AddSingleton(Mock.Of<ITripEditorGeocodeSearchService>());
        });

    /// <summary>Uses an actual credential from the isolated candidate.</summary>
    private static HttpClient Client(WebApplication app, Trip trip)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", trip.UserId);
        return client;
    }

    /// <summary>Reads the framework key ID without capturing the proof in test output.</summary>
    private static Guid KeyId(JsonElement permit) =>
        new(WebEncoders.Base64UrlDecode(permit.GetProperty("proof").GetString()!).AsSpan(4, 16));

    /// <summary>Verifies the pending statement is waiting in PostgreSQL before stepping the clock.</summary>
    private static async Task WaitForBlockAsync(ApplicationDbContext blocker, int process)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await blocker.Database.SqlQuery<bool>($"SELECT cardinality(pg_blocking_pids({process})) > 0 AS \"Value\"").SingleAsync())
                return;
            await Task.Delay(20);
        }
        Assert.Fail("The marked write did not reach the Trip row lock.");
    }

    /// <summary>Models clock corrections without changing shared PostgreSQL, the system clock or the protected permit.</summary>
    private sealed class DatabaseClock : DbCommandInterceptor
    {
        internal volatile int ShiftSeconds;

        /// <summary>Shifts database wall-clock evaluation for the receipt statements only.</summary>
        private void Shift(DbCommand command)
        {
            if (command.CommandText.Contains("trip_metadata_proof_authority"))
                command.CommandText = command.CommandText.Replace("clock_timestamp()", FormattableString.Invariant($"(clock_timestamp() + ({ShiftSeconds} * interval '1 second'))"));
        }

        /// <summary>Applies the controlled clock to authoritative admission and observation reads.</summary>
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Shift(command);
            return ValueTask.FromResult(result);
        }

        /// <summary>Applies the same clock to maintenance and cleanup.</summary>
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Shift(command);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Signals the real lock request and allows a writer to pause after its final admission query.</summary>
    private sealed class WriteGate : DbCommandInterceptor
    {
        internal TaskCompletionSource<int> TripLock { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Update { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool PauseUpdate { get; init; }

        /// <summary>Captures the backend PID requesting the authoritative Trip lock.</summary>
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("public.\"Trips\"") && command.CommandText.Contains("FOR UPDATE"))
                TripLock.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID);
            return ValueTask.FromResult(result);
        }

        /// <summary>Pauses the exact domain update while its operation fence is held.</summary>
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (PauseUpdate && command.CommandText.StartsWith("UPDATE \"Trips\"", StringComparison.Ordinal))
            {
                Update.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
