using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NetTopologySuite.Geometries;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class AreaConditionalPostgresTests
{
    /// <summary>Post-wait originals, parent binding and active authority govern every guarded write.</summary>
    [PostgresTheory]
    [InlineData(false, "Areas", "notes", 409)]
    [InlineData(true, "Areas", "notes", 409)]
    [InlineData(true, "Areas", "name", 409)]
    [InlineData(true, "Areas", "fill", 409)]
    [InlineData(true, "Areas", "hole", 409)]
    [InlineData(true, "Areas", "precision", 409)]
    [InlineData(false, "Areas", "parent", 409)]
    [InlineData(true, "Areas", "parent", 409)]
    [InlineData(false, "Trips", "token", 401)]
    [InlineData(false, "Trips", "inactive", 401)]
    [InlineData(true, "Trips", "inactive", 401)]
    [InlineData(false, "Regions", "region-owner", 404)]
    [InlineData(true, "Trips", "trip-owner", 404)]
    public async Task LockWait_RevalidatesOriginalScopeAndAuthority(bool web, string lockedTable, string drift, int status)
    {
        var area = await SeedAsync();
        var other = await SeedAsync();
        var destination = new Region { Id = Guid.NewGuid(), TripId = area.Region.TripId, UserId = area.Region.UserId, Name = "Unassigned Places", DisplayOrder = 0 };
        await using var db = fixture.CreateContext();
        db.Regions.Add(destination);
        await db.SaveChangesAsync();
        var beforeStamp = await db.Trips.Where(t => t.Id == area.Region.TripId).Select(t => t.UpdatedAt).SingleAsync();
        var probe = new AreaProbe(lockedTable);
        await using var app = await StartAsync(db, [probe]);
        using var client = web ? await WebAsync(app, area.Region.UserId) : Bearer(app, area.Region.UserId);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        var lockId = lockedTable == "Areas" ? area.Id : lockedTable == "Regions" ? area.RegionId : area.Region.TripId;
        if (lockedTable == "Areas") await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Areas\" WHERE \"Id\" = {lockId} FOR UPDATE");
        else if (lockedTable == "Regions") await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Regions\" WHERE \"Id\" = {lockId} FOR UPDATE");
        else await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Trips\" WHERE \"Id\" = {lockId} FOR UPDATE");
        var key = drift == "name" ? "name" : drift == "fill" ? "fillHex" : drift is "hole" or "precision" ? "geometry" : web ? "notesHtml" : "notes";
        var original = key == "name" ? area.Name : key == "fillHex" ? area.FillHex : key == "geometry" ? Fingerprint(area) : area.Notes;
        var desired = key == "geometry" ? (object)Geometry(Polygon(30)) : key == "fillHex" ? "#abcdef" : "Must not commit";
        var pending = PatchAsync(client, area, web, new Dictionary<string, object?> { [key] = original }, new Dictionary<string, object?> { [key] = desired });
        try
        {
            await WaitForLockAsync(probe, 0);
            if (drift == "notes") await blocker.Areas.Where(a => a.Id == area.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Notes, "Changed during wait"));
            if (drift == "name") await blocker.Areas.Where(a => a.Id == area.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Name, "Changed during wait"));
            if (drift == "fill") await blocker.Areas.Where(a => a.Id == area.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.FillHex, "#654321"));
            if (drift is "hole" or "precision")
            {
                var changed = (Polygon)area.Geometry.Copy();
                if (drift == "hole") changed.GetInteriorRingN(0).CoordinateSequence.SetX(1, changed.GetInteriorRingN(0).Coordinates[1].X + 0.125);
                else changed.ExteriorRing.CoordinateSequence.SetX(1, changed.ExteriorRing.Coordinates[1].X + 1e-13);
                changed.GeometryChanged();
                await blocker.Areas.Where(a => a.Id == area.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Geometry, changed));
            }
            if (drift == "parent") await blocker.Areas.Where(a => a.Id == area.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.RegionId, destination.Id));
            if (drift == "region-owner") await blocker.Regions.Where(r => r.Id == area.RegionId).ExecuteUpdateAsync(s => s.SetProperty(r => r.UserId, other.Region.UserId));
            if (drift == "trip-owner") await blocker.Trips.Where(t => t.Id == area.Region.TripId).ExecuteUpdateAsync(s => s.SetProperty(t => t.UserId, other.Region.UserId));
            await using var authority = fixture.CreateContext();
            if (drift == "inactive") await authority.Users.Where(u => u.Id == area.Region.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
            if (drift == "token") await authority.ApiTokens.Where(t => t.UserId == area.Region.UserId).ExecuteDeleteAsync();
            await transaction.CommitAsync();
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(status, (int)response.StatusCode);
            Assert.Empty(probe.Updates);
            Assert.Empty(_warmup.Invocations);
            await using var verify = fixture.CreateContext();
            Assert.Equal(beforeStamp, (await verify.Trips.FindAsync(area.Region.TripId))!.UpdatedAt);
            Assert.Equal(drift == "notes" ? "Changed during wait" : area.Notes, (await verify.Areas.FindAsync(area.Id))!.Notes);
            output.WriteLine($"Observed physical {lockedTable} wait; web={web}; drift={drift}; rejected {status}; no operation SQL writes/cache calls.");
        }
        finally { await transaction.DisposeAsync(); using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    /// <summary>Both actual Web/bearer requests queue at the Area lock; disjoint edits survive either admission order.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QueuedBidirectionalDisjointEdits_BothPersist(bool webFirst)
    {
        var area = await SeedAsync();
        var probe = new AreaProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, area.Region.UserId);
        using var bearer = Bearer(app, area.Region.UserId);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Areas\" WHERE \"Id\" = {area.Id} FOR UPDATE");
        Task<HttpResponseMessage> Save(bool isWeb) => isWeb
            ? PatchAsync(web, area, true, new { name = area.Name, fillHex = area.FillHex, geometry = Fingerprint(area) },
                new { name = "Concurrent Web name", fillHex = "#abcdef", geometry = Geometry(Polygon(20)) })
            : PatchAsync(bearer, area, false, new { notes = area.Notes }, new { notes = "Concurrent Mobile notes" });
        var first = Save(webFirst);
        Task<HttpResponseMessage>? second = null;
        try
        {
            await WaitForLockAsync(probe, 0);
            second = Save(!webFirst);
            await WaitForLockAsync(probe, 1);
            await transaction.CommitAsync();
            using var one = await first.WaitAsync(TimeSpan.FromSeconds(10));
            using var two = await second.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(HttpStatusCode.OK, one.StatusCode);
            Assert.Equal(HttpStatusCode.OK, two.StatusCode);
            await using var verify = fixture.CreateContext();
            var stored = await verify.Areas.SingleAsync(a => a.Id == area.Id);
            Assert.Equal(("Concurrent Web name", "Concurrent Mobile notes", "#abcdef", area.DisplayOrder),
                (stored.Name, stored.Notes, stored.FillHex, stored.DisplayOrder));
            AssertPolygon(Polygon(20), stored.Geometry);
            output.WriteLine($"Two routed requests physically blocked; webFirst={webFirst}; disjoint changes committed in both orders.");
        }
        finally
        {
            await transaction.DisposeAsync();
            using var one = await first.WaitAsync(TimeSpan.FromSeconds(10));
            if (second != null) { using var two = await second.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
    }

    /// <summary>Rollback and actual commit followed by lost acknowledgement both return uncertainty, never a false 409.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitFailureOrLostAcknowledgement_NeverClaimsKnownUnapplied(bool committed)
    {
        var area = await SeedAsync();
        var probe = new AreaProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe, new FailAreaCommit(committed)]);
        using var bearer = Bearer(app, area.Region.UserId);
        using var response = await PatchAsync(bearer, area, false, new { notes = area.Notes }, new { notes = "Attempted notes" });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await using var verify = fixture.CreateContext();
        Assert.Equal(committed ? "Attempted notes" : area.Notes, (await verify.Areas.FindAsync(area.Id))!.Notes);
        var stamp = (await verify.Trips.FindAsync(area.Region.TripId))!.UpdatedAt;
        if (committed) Assert.True(stamp > area.Region.Trip.UpdatedAt);
        else Assert.Equal(area.Region.Trip.UpdatedAt, stamp);
        Assert.Equal(2, probe.Updates.Count);
        Assert.Empty(_warmup.Invocations);
        output.WriteLine($"Controlled commit boundary failure; committed={committed}; HTTP 500 remains unknown to caller; no automatic replay.");
    }

    /// <summary>Cancellation at a real Area lock leaves content and timestamp intact and never retries.</summary>
    [PostgresFact]
    public async Task CancelledAreaWait_DoesNotWriteOrRetry()
    {
        var area = await SeedAsync();
        var probe = new AreaProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var bearer = Bearer(app, area.Region.UserId);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Areas\" WHERE \"Id\" = {area.Id} FOR UPDATE");
        using var cancellation = new CancellationTokenSource();
        var pending = bearer.PatchAsJsonAsync($"/api/trips/{area.Region.TripId}/conditional/areas/{area.Id}",
            new { expected = new { regionId = area.RegionId, notes = area.Notes }, changes = new { notes = "Cancelled" } }, cancellation.Token);
        try
        {
            await WaitForLockAsync(probe, 0);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        }
        finally { await transaction.RollbackAsync(); }
        await using var verify = fixture.CreateContext();
        Assert.Equal(area.Notes, (await verify.Areas.FindAsync(area.Id))!.Notes);
        Assert.Equal(area.Region.Trip.UpdatedAt, (await verify.Trips.FindAsync(area.Region.TripId))!.UpdatedAt);
        Assert.Empty(probe.Updates);
        Assert.Empty(_warmup.Invocations);
    }

    /// <summary>Injects loss before or after the real transaction commit without introducing a receipt/retry facility.</summary>
    private sealed class FailAreaCommit(bool afterCommit) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData data,
            InterceptionResult result, CancellationToken cancellationToken = default) => afterCommit
                ? ValueTask.FromResult(result) : throw new IOException("Controlled precommit failure");
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken cancellationToken = default) =>
            afterCommit ? throw new IOException("Controlled loss of commit acknowledgement") : Task.CompletedTask;
    }

    /// <summary>Captures actual property footprints and connection IDs without logging private parameter values.</summary>
    private sealed class AreaProbe(string table = "Areas") : DbCommandInterceptor
    {
        internal ConcurrentQueue<int> LockProcesses { get; } = new();
        internal ConcurrentQueue<string> Updates { get; } = new();
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            if (command.CommandText.Contains($"public.\"{table}\"") && command.CommandText.Contains("FOR UPDATE"))
                LockProcesses.Enqueue(((NpgsqlConnection)command.Connection!).ProcessID);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }
        private void Record(DbCommand command)
        {
            if (command.CommandText.StartsWith("UPDATE ")) Updates.Enqueue(command.CommandText);
        }
    }

    /// <summary>Requires a physical PostgreSQL blocker; command dispatch or an elapsed delay alone is insufficient.</summary>
    private async Task WaitForLockAsync(AreaProbe probe, int index)
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (probe.LockProcesses.Count > index)
            {
                await using var query = new NpgsqlCommand("SELECT cardinality(pg_blocking_pids($1)) > 0", connection);
                query.Parameters.AddWithValue(probe.LockProcesses.ElementAt(index));
                if ((bool)(await query.ExecuteScalarAsync())!) return;
            }
            await Task.Delay(20);
        }
        Assert.Fail("The routed request did not reach a PostgreSQL row-lock wait.");
    }
}
