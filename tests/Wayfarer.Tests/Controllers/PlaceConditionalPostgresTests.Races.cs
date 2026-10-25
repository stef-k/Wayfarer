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

public sealed partial class PlaceConditionalPostgresTests
{
    /// <summary>Post-wait originals, parent binding and active authority govern every guarded write.</summary>
    [PostgresTheory]
    [InlineData(false, "Places", "notes", 409)]
    [InlineData(true, "Places", "notes", 409)]
    [InlineData(true, "Places", "name", 409)]
    [InlineData(true, "Places", "icon", 409)]
    [InlineData(false, "Places", "color", 409)]
    [InlineData(false, "Places", "place-owner", 404)]
    [InlineData(false, "Places", "parent", 409)]
    [InlineData(true, "Places", "parent", 409)]
    [InlineData(false, "Trips", "token", 401)]
    [InlineData(false, "Trips", "inactive", 401)]
    [InlineData(true, "Trips", "inactive", 401)]
    [InlineData(false, "Regions", "region-owner", 404)]
    [InlineData(true, "Trips", "trip-owner", 404)]
    public async Task LockWait_RevalidatesOriginalScopeAndAuthority(bool web, string lockedTable, string drift, int status)
    {
        var place = await SeedAsync();
        var other = await SeedAsync();
        var destination = new Region { Id = Guid.NewGuid(), TripId = place.Region.TripId, UserId = place.Region.UserId, Name = "Unassigned Places", DisplayOrder = 0 };
        await using var db = fixture.CreateContext();
        db.Regions.Add(destination);
        await db.SaveChangesAsync();
        var beforeStamp = await db.Trips.Where(t => t.Id == place.Region.TripId).Select(t => t.UpdatedAt).SingleAsync();
        var probe = new PlaceProbe(lockedTable);
        await using var app = await StartAsync(db, [probe]);
        using var client = web ? await WebAsync(app, place.Region.UserId) : Bearer(app, place.Region.UserId);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        var lockId = lockedTable == "Places" ? place.Id : lockedTable == "Regions" ? place.RegionId : place.Region.TripId;
        if (lockedTable == "Places") await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Places\" WHERE \"Id\" = {lockId} FOR UPDATE");
        else if (lockedTable == "Regions") await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Regions\" WHERE \"Id\" = {lockId} FOR UPDATE");
        else await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Trips\" WHERE \"Id\" = {lockId} FOR UPDATE");
        var key = drift == "name" ? "name" : drift == "icon" ? "iconName" : drift == "color" ? "markerColor" : web ? "notesHtml" : "notes";
        var original = key == "name" ? place.Name : key == "iconName" ? place.IconName : key == "markerColor" ? place.MarkerColor : place.Notes;
        var desired = key == "iconName" ? "star" : key == "markerColor" ? "bg-red" : "Must not commit";
        var pending = PatchAsync(client, place, web, new Dictionary<string, object?> { [key] = original }, new Dictionary<string, object?> { [key] = desired });
        try
        {
            await WaitForLockAsync(probe, 0);
            if (drift == "notes") await blocker.Places.Where(a => a.Id == place.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Notes, "Changed during wait"));
            if (drift == "name") await blocker.Places.Where(a => a.Id == place.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Name, "Changed during wait"));
            if (drift == "icon") await blocker.Places.Where(p => p.Id == place.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.IconName, "star"));
            if (drift == "color") await blocker.Places.Where(p => p.Id == place.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.MarkerColor, "bg-red"));
            if (drift == "place-owner") await blocker.Places.Where(p => p.Id == place.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.UserId, other.Region.UserId));
            if (drift == "parent") await blocker.Places.Where(a => a.Id == place.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.RegionId, destination.Id));
            if (drift == "region-owner") await blocker.Regions.Where(r => r.Id == place.RegionId).ExecuteUpdateAsync(s => s.SetProperty(r => r.UserId, other.Region.UserId));
            if (drift == "trip-owner") await blocker.Trips.Where(t => t.Id == place.Region.TripId).ExecuteUpdateAsync(s => s.SetProperty(t => t.UserId, other.Region.UserId));
            await using var authority = fixture.CreateContext();
            if (drift == "inactive") await authority.Users.Where(u => u.Id == place.Region.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
            if (drift == "token") await authority.ApiTokens.Where(t => t.UserId == place.Region.UserId).ExecuteDeleteAsync();
            await transaction.CommitAsync();
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(status, (int)response.StatusCode);
            Assert.Empty(probe.Updates);
            Assert.Empty(_warmup.Invocations);
            await using var verify = fixture.CreateContext();
            Assert.Equal(beforeStamp, (await verify.Trips.FindAsync(place.Region.TripId))!.UpdatedAt);
            Assert.Equal(drift == "notes" ? "Changed during wait" : place.Notes, (await verify.Places.FindAsync(place.Id))!.Notes);
            output.WriteLine($"Observed physical {lockedTable} wait; web={web}; drift={drift}; rejected {status}; no operation SQL writes/cache calls.");
        }
        finally { await transaction.DisposeAsync(); using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    /// <summary>Both actual Web/bearer requests queue at the Place lock; disjoint edits survive either admission order.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QueuedBidirectionalDisjointEdits_BothPersist(bool webFirst)
    {
        var place = await SeedAsync();
        var probe = new PlaceProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, place.Region.UserId);
        using var bearer = Bearer(app, place.Region.UserId);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Places\" WHERE \"Id\" = {place.Id} FOR UPDATE");
        Task<HttpResponseMessage> Save(bool isWeb) => isWeb
            ? PatchAsync(web, place, true, new { name = place.Name, iconName = place.IconName },
                new { name = "Concurrent Web name", iconName = "star" })
            : PatchAsync(bearer, place, false, new { notes = place.Notes, markerColor = place.MarkerColor }, new { notes = "Concurrent Mobile notes", markerColor = "bg-red" });
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
            var stored = await verify.Places.SingleAsync(a => a.Id == place.Id);
            Assert.Equal(("Concurrent Web name", "Concurrent Mobile notes", "star", "bg-red", place.DisplayOrder),
                (stored.Name, stored.Notes, stored.IconName, stored.MarkerColor, stored.DisplayOrder));
            Assert.Equal(place.Location!.AsText(), stored.Location!.AsText());
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
        var place = await SeedAsync();
        var probe = new PlaceProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe, new FailPlaceCommit(committed)]);
        using var bearer = Bearer(app, place.Region.UserId);
        using var response = await PatchAsync(bearer, place, false, new { notes = place.Notes }, new { notes = "Attempted notes" });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await using var verify = fixture.CreateContext();
        Assert.Equal(committed ? "Attempted notes" : place.Notes, (await verify.Places.FindAsync(place.Id))!.Notes);
        var stamp = (await verify.Trips.FindAsync(place.Region.TripId))!.UpdatedAt;
        if (committed) Assert.True(stamp > place.Region.Trip.UpdatedAt);
        else Assert.Equal(place.Region.Trip.UpdatedAt, stamp);
        Assert.Equal(2, probe.Updates.Count);
        Assert.Empty(_warmup.Invocations);
        output.WriteLine($"Controlled commit boundary failure; committed={committed}; HTTP 500 remains unknown to caller; no automatic replay.");
    }

    /// <summary>Cancellation at a real Place lock leaves content and timestamp intact and never retries.</summary>
    [PostgresFact]
    public async Task CancelledPlaceWait_DoesNotWriteOrRetry()
    {
        var place = await SeedAsync();
        var probe = new PlaceProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var bearer = Bearer(app, place.Region.UserId);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Places\" WHERE \"Id\" = {place.Id} FOR UPDATE");
        using var cancellation = new CancellationTokenSource();
        var pending = bearer.PatchAsJsonAsync($"/api/trips/{place.Region.TripId}/conditional/places/{place.Id}",
            new { expected = new { regionId = place.RegionId, notes = place.Notes }, changes = new { notes = "Cancelled" } }, cancellation.Token);
        try
        {
            await WaitForLockAsync(probe, 0);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        }
        finally { await transaction.RollbackAsync(); }
        await using var verify = fixture.CreateContext();
        Assert.Equal(place.Notes, (await verify.Places.FindAsync(place.Id))!.Notes);
        Assert.Equal(place.Region.Trip.UpdatedAt, (await verify.Trips.FindAsync(place.Region.TripId))!.UpdatedAt);
        Assert.Empty(probe.Updates);
        Assert.Empty(_warmup.Invocations);
    }

    /// <summary>Injects loss before or after the real transaction commit without introducing a receipt/retry facility.</summary>
    private sealed class FailPlaceCommit(bool afterCommit) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData data,
            InterceptionResult result, CancellationToken cancellationToken = default) => afterCommit
                ? ValueTask.FromResult(result) : throw new IOException("Controlled precommit failure");
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken cancellationToken = default) =>
            afterCommit ? throw new IOException("Controlled loss of commit acknowledgement") : Task.CompletedTask;
    }

    /// <summary>Captures actual property footprints and connection IDs without logging private parameter values.</summary>
    private sealed class PlaceProbe(string table = "Places") : DbCommandInterceptor
    {
        internal ConcurrentQueue<int> LockProcesses { get; } = new();
        internal ConcurrentQueue<string> Updates { get; } = new();
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
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
            if (command.CommandText.Contains($"public.\"{table}\"") && command.CommandText.Contains("FOR UPDATE"))
                LockProcesses.Enqueue(((NpgsqlConnection)command.Connection!).ProcessID);
            if (command.CommandText.StartsWith("UPDATE ")) Updates.Enqueue(command.CommandText);
        }
    }

    /// <summary>Requires a physical PostgreSQL blocker; command dispatch or an elapsed delay alone is insufficient.</summary>
    private async Task WaitForLockAsync(PlaceProbe probe, int index)
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
