using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class TripMetadataConditionalPostgresTests
{
    /// <summary>Final state and authority are checked after a genuine PostgreSQL row-lock wait.</summary>
    [PostgresTheory]
    [InlineData(false, "name", 409)]
    [InlineData(true, "name", 409)]
    [InlineData(false, "token", 401)]
    [InlineData(true, "inactive", 401)]
    [InlineData(false, "owner", 404)]
    public async Task LockWait_RevalidatesOriginalAndAuthority(bool webRoute, string drift, int expectedStatus)
    {
        var trip = await SeedAsync();
        var probe = new MutationProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var client = webRoute ? await WebAsync(app, trip.UserId) : Bearer(app, trip.UserId);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Trips\" WHERE \"Id\" = {trip.Id} FOR UPDATE");
        var pending = PatchAsync(client, trip.Id, webRoute, new { name = trip.Name }, new { name = "Must not commit" });
        try
        {
            await probe.LockStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForBlockAsync(probe.ProcessId);
            if (drift == "name") await blocker.Trips.Where(t => t.Id == trip.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Name, "During wait"));
            if (drift == "owner")
            {
                var other = await fixture.CreateUserAsync();
                await blocker.Trips.Where(t => t.Id == trip.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.UserId, other.Id));
            }
            await using var authority = fixture.CreateContext();
            if (drift == "token") await authority.ApiTokens.Where(t => t.UserId == trip.UserId).ExecuteDeleteAsync();
            if (drift == "inactive") await authority.Users.Where(u => u.Id == trip.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
            await transaction.CommitAsync();
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(expectedStatus, (int)response.StatusCode);
            await using var verification = fixture.CreateContext();
            var stored = await verification.Trips.SingleAsync(t => t.Id == trip.Id);
            Assert.Equal(drift == "name" ? "During wait" : trip.Name, stored.Name);
            Assert.Equal(trip.Notes, stored.Notes);
            Assert.Equal(trip.UpdatedAt, stored.UpdatedAt);
            Assert.Empty(_warmup.Invocations);
            Assert.Empty(_thumbnails.Invocations);
        }
        finally
        {
            await transaction.DisposeAsync();
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>Cookie and incoming bearer remain separate authorities; hidden owner scope reveals no current values.</summary>
    [PostgresFact]
    public async Task Routes_KeepAuthorityAndAntiforgeryBoundaries()
    {
        var trip = await SeedAsync();
        var other = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var cookie = await WebAsync(app, trip.UserId);
        using var bearer = Bearer(app, trip.UserId);
        using var foreignCookie = await WebAsync(app, other.UserId);
        using var foreignBearer = Bearer(app, other.UserId);
        using var noAntiforgery = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(noAntiforgery, app.Services, trip.UserId);
        using var cookieOnBearer = await PatchAsync(cookie, trip.Id, false, new { name = trip.Name }, new { name = "No" });
        using var bearerOnCookie = await PatchAsync(bearer, trip.Id, true, new { name = trip.Name }, new { name = "No" });
        using var missingAntiforgery = await PatchAsync(noAntiforgery, trip.Id, true, new { name = trip.Name }, new { name = "No" });
        using var nonOwner = await PatchAsync(foreignCookie, trip.Id, true, new { name = trip.Name }, new { name = "No" });
        using var invalidBearer = await PatchAsync(foreignBearer, trip.Id, false, new { name = trip.Name }, new { name = "No" });
        Assert.Equal(HttpStatusCode.Unauthorized, cookieOnBearer.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, bearerOnCookie.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingAntiforgery.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nonOwner.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, invalidBearer.StatusCode);
        Assert.Empty(_warmup.Invocations);
        Assert.Empty(_thumbnails.Invocations);
    }

    /// <summary>Existing share-progress routes recheck publication when their update waits behind a privacy commit.</summary>
    [PostgresTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task SharingAfterPrivacy_CannotLeavePrivateTripSharing(bool editorRoute, bool privacyFirst)
    {
        var trip = await SeedAsync();
        var probe = new MutationProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        // Each request family has its own probe now that both acquire the same Trip lock first.
        var sharingProbe = new MutationProbe();
        await using var sharingApp = await StartAsync(db, [sharingProbe]);
        using var web = await WebAsync(sharingApp, trip.UserId);
        using var privacyWeb = await WebAsync(app, trip.UserId);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Trips\" WHERE \"Id\" = {trip.Id} FOR UPDATE");
        Task<HttpResponseMessage> Share() => editorRoute
            ? web.PatchAsJsonAsync($"/api/trips/{trip.Id}/editor/share-progress", new { enabled = true })
            : web.PostAsync($"/User/Trip/ToggleShareProgress/{trip.Id}?enabled=true", null);
        Task<HttpResponseMessage> Privatize() => PatchAsync(privacyWeb, trip.Id, true, new { isPublic = true }, new { isPublic = false });
        var first = privacyFirst ? Privatize() : Share();
        Task<HttpResponseMessage>? second = null;
        try
        {
            await (privacyFirst ? probe.LockStarted.Task : sharingProbe.LockStarted.Task).WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForBlockAsync(privacyFirst ? probe.ProcessId : sharingProbe.ProcessId);
            second = privacyFirst ? Share() : Privatize();
            await (privacyFirst ? sharingProbe.LockStarted.Task : probe.LockStarted.Task).WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForBlockAsync(privacyFirst ? sharingProbe.ProcessId : probe.ProcessId);
            await transaction.CommitAsync();
            using var privacy = await (privacyFirst ? first : second).WaitAsync(TimeSpan.FromSeconds(10));
            using var sharing = await (privacyFirst ? second : first).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(HttpStatusCode.OK, privacy.StatusCode);
            Assert.Equal(editorRoute && privacyFirst ? HttpStatusCode.BadRequest : HttpStatusCode.OK, sharing.StatusCode);
            if (!editorRoute && privacyFirst) Assert.Contains("\"success\":false", await sharing.Content.ReadAsStringAsync());
            await using var verification = fixture.CreateContext();
            var stored = await verification.Trips.SingleAsync(t => t.Id == trip.Id);
            Assert.False(stored.IsPublic || stored.ShareProgressEnabled);
        }
        finally
        {
            await transaction.DisposeAsync();
            using var firstResponse = await first.WaitAsync(TimeSpan.FromSeconds(10));
            if (second != null) { using var secondResponse = await second.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
    }

    /// <summary>A failed final commit rolls back all fields and privacy effects and performs no external work.</summary>
    [PostgresFact]
    public async Task FailedCommit_RollsBackEntireDirtyFootprint()
    {
        var trip = await SeedAsync();
        var probe = new MutationProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe, new FailMetadataCommit()]);
        using var web = await WebAsync(app, trip.UserId);
        using var response = await PatchAsync(web, trip.Id, true, new { name = trip.Name, isPublic = true, notesHtml = trip.Notes },
            new { name = "Uncommitted", isPublic = false, notesHtml = "<p>Uncommitted</p>" });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await using var verification = fixture.CreateContext();
        var stored = await verification.Trips.SingleAsync(t => t.Id == trip.Id);
        Assert.Equal((trip.Name, trip.Notes, trip.IsPublic, trip.ShareProgressEnabled, trip.UpdatedAt),
            (stored.Name, stored.Notes, stored.IsPublic, stored.ShareProgressEnabled, stored.UpdatedAt));
        Assert.Single(probe.Updates);
        output.WriteLine(probe.Updates.Single());
        Assert.Empty(_warmup.Invocations);
        Assert.Empty(_thumbnails.Invocations);
    }

    /// <summary>A lost success response is uncertain to the caller even when a fresh context proves actual commit.</summary>
    [PostgresFact]
    public async Task LostReplyAfterCommit_IsNotNoEffectConflictOrReceipt()
    {
        var trip = await SeedAsync();
        var probe = new MutationProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe], app => app.Use(async (context, next) =>
        {
            if (!context.Request.Path.Value!.EndsWith("/conditional")) { await next(); return; }
            var transport = context.Response.Body;
            await using var buffer = new MemoryStream();
            context.Response.Body = buffer;
            try { await next(); }
            finally { context.Response.Body = transport; }
            throw new IOException("Controlled lost response after committed action");
        }));
        using var bearer = Bearer(app, trip.UserId);
        using var response = await PatchAsync(bearer, trip.Id, false, new { notes = trip.Notes }, new { notes = "Actually committed" });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await using var verification = fixture.CreateContext();
        var stored = await verification.Trips.SingleAsync(t => t.Id == trip.Id);
        Assert.Equal("Actually committed", stored.Notes);
        Assert.Equal(trip.Name, stored.Name);
        var sql = Assert.Single(probe.Updates);
        Assert.Contains("\"Notes\" =", sql);
        Assert.DoesNotContain("\"Name\" =", sql);
        Assert.DoesNotContain("\"IsPublic\" =", sql);
        output.WriteLine(sql);
        Assert.Single(_warmup.Invocations);
    }

    /// <summary>Cancellation during a lock wait is a transport failure, with no automatic replay or false conflict receipt.</summary>
    [PostgresFact]
    public async Task CancelledLockWait_DoesNotReportDefinitiveConflict()
    {
        var trip = await SeedAsync();
        var probe = new MutationProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var bearer = Bearer(app, trip.UserId);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Trips\" WHERE \"Id\" = {trip.Id} FOR UPDATE");
        using var cancellation = new CancellationTokenSource();
        var pending = bearer.PatchAsJsonAsync($"/api/trips/{trip.Id}/conditional",
            new { expected = new { name = trip.Name }, changes = new { name = "Cancelled edit" } }, cancellation.Token);
        await probe.LockStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForBlockAsync(probe.ProcessId);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await transaction.RollbackAsync();
        await using var verification = fixture.CreateContext();
        Assert.Equal(trip.Name, (await verification.Trips.FindAsync(trip.Id))!.Name);
        Assert.Empty(probe.Updates);
        Assert.Empty(_warmup.Invocations);
    }

    /// <summary>Retains the known legacy counterexample: a stale full Web draft still overwrites a later bearer Notes edit.</summary>
    [PostgresFact]
    public async Task LegacyCompatibility_RemainsUnconditional_WithExplicitOverwriteRisk()
    {
        var trip = await SeedAsync();
        var probe = new MutationProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, trip.UserId);
        using var bearer = Bearer(app, trip.UserId);
        using var mobile = await bearer.PutAsJsonAsync($"/api/trips/{trip.Id}", new { notes = "Later legacy bearer notes" });
        Assert.Equal(HttpStatusCode.OK, mobile.StatusCode);
        using var staleWeb = await web.PatchAsJsonAsync($"/api/trips/{trip.Id}/editor/metadata", new
        {
            name = trip.Name, notesHtml = trip.Notes, isPublic = true,
            coverImage = new { rawUrl = "https://example.test/legacy-cover.jpg" }, center = new { latitude = 1, longitude = 2 }, zoom = 3
        });
        Assert.Equal(HttpStatusCode.OK, staleWeb.StatusCode);
        await using var verification = fixture.CreateContext();
        Assert.Equal(trip.Notes, (await verification.Trips.FindAsync(trip.Id))!.Notes);
        output.WriteLine("Known unprotected legacy full-draft SQL:");
        output.WriteLine(probe.Updates.Last());
        Assert.Contains("\"Notes\" =", probe.Updates.Last());
    }

    /// <summary>Observes a real server-side blocker rather than assuming a command-start callback proves waiting.</summary>
    private async Task WaitForBlockAsync(int processId)
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand("SELECT cardinality(pg_blocking_pids($1)) > 0", connection);
        query.Parameters.AddWithValue(processId);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if ((bool)(await query.ExecuteScalarAsync())!) return;
            await Task.Delay(20);
        }
        Assert.Fail("The routed request did not reach a PostgreSQL row-lock wait.");
    }

    /// <summary>Records actual production SQL and exposes command identities without modifying query results.</summary>
    private sealed class MutationProbe : DbCommandInterceptor
    {
        internal TaskCompletionSource LockStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource UpdateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ProcessId { get; private set; }
        internal List<string> Updates { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE ")) Updates.Add(command.CommandText);
            if (command.CommandText.Contains("FOR UPDATE"))
            {
                ProcessId = ((NpgsqlConnection)command.Connection!).ProcessID;
                LockStarted.TrySetResult();
            }
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE "))
            {
                Updates.Add(command.CommandText);
                ProcessId = ((NpgsqlConnection)command.Connection!).ProcessID;
                UpdateStarted.TrySetResult();
            }
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Injects one failure after domain SQL but before commit, at EF's existing transaction seam.</summary>
    private sealed class FailMetadataCommit : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData data,
            InterceptionResult result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Controlled metadata commit failure");
    }
}
