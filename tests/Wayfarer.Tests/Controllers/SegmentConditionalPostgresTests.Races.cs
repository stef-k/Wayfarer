using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Tests.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class SegmentConditionalPostgresTests
{
    /// <summary>A physical child/parent wait must precede fresh scope, original and incoming authority validation.</summary>
    [PostgresTheory]
    [InlineData(false, "Segments", "notes", 409)]
    [InlineData(true, "Segments", "notes", 409)]
    [InlineData(false, "Segments", "owner", 404)]
    [InlineData(true, "Segments", "trip", 409)]
    [InlineData(true, "Trips", "owner", 404)]
    [InlineData(false, "Trips", "token", 401)]
    [InlineData(false, "Trips", "inactive", 401)]
    [InlineData(true, "Trips", "inactive", 401)]
    public async Task LockWait_RevalidatesScopeOriginalAndAuthority(bool web, string table, string drift, int status)
    {
        var seed = await SeedAsync();
        var other = await SeedAsync();
        await using var db = fixture.CreateContext();
        // Same-owner Trip drift is a stale scope, rather than evidence authorizing a move.
        if (drift == "trip") await db.Trips.Where(t => t.Id == other.TripId).ExecuteUpdateAsync(s => s.SetProperty(t => t.UserId, seed.UserId));
        var before = await db.Trips.AsNoTracking().SingleAsync(t => t.Id == seed.TripId);
        var probe = new SegmentProbe(table);
        await using var app = await StartAsync(db, probe);
        using var client = web ? await WebAsync(app, seed.UserId) : Bearer(app, seed.UserId);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        if (table == "Segments") await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Segments\" WHERE \"Id\" = {seed.SegmentId} FOR UPDATE");
        else await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Trips\" WHERE \"Id\" = {seed.TripId} FOR UPDATE");
        var pending = PatchAsync(client, seed, web, "original notes", "Must not commit");
        try
        {
            await WaitForLockAsync(probe, 0);
            if (drift == "notes") await blocker.Segments.Where(s => s.Id == seed.SegmentId).ExecuteUpdateAsync(s => s.SetProperty(s => s.Notes, "Other notes"));
            if (drift == "trip") await blocker.Segments.Where(s => s.Id == seed.SegmentId).ExecuteUpdateAsync(s => s.SetProperty(s => s.TripId, other.TripId));
            if (drift == "owner" && table == "Segments") await blocker.Segments.Where(s => s.Id == seed.SegmentId).ExecuteUpdateAsync(s => s.SetProperty(s => s.UserId, other.UserId));
            if (drift == "owner" && table == "Trips") await blocker.Trips.Where(t => t.Id == seed.TripId).ExecuteUpdateAsync(s => s.SetProperty(s => s.UserId, other.UserId));
            await using var authority = fixture.CreateContext();
            if (drift == "inactive") await authority.Users.Where(u => u.Id == seed.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
            if (drift == "token") await authority.ApiTokens.Where(t => t.UserId == seed.UserId).ExecuteDeleteAsync();
            await transaction.CommitAsync();
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(status, (int)response.StatusCode);
            Assert.Empty(probe.Updates);
            await using var verify = fixture.CreateContext();
            Assert.Equal(drift == "notes" ? "Other notes" : "original notes", (await ReadAsync(verify, seed)).Notes);
            Assert.Equal(before.UpdatedAt, (await verify.Trips.FindAsync(seed.TripId))!.UpdatedAt);
            Assert.Empty(_warmup.Invocations);
            output.WriteLine($"Observed PostgreSQL {table} lock wait; web={web}; drift={drift}; {status}; zero operation SQL writes.");
        }
        finally { await transaction.DisposeAsync(); using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    /// <summary>The real complete writer and conditional Notes writer use compatible child-first locks in either queue order.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QueuedRouteAndNotes_PreserveBoth(bool notesFirst)
    {
        var seed = await SeedAsync();
        var probe = new SegmentProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, probe);
        using var web = await WebAsync(app, seed.UserId);
        using var bearer = Bearer(app, seed.UserId);
        var dto = await PublishedAsync(app, seed);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Segments\" WHERE \"Id\" = {seed.SegmentId} FOR UPDATE");
        Task<HttpResponseMessage> Save(bool notes) => notes
            ? PatchAsync(bearer, seed, false, dto.NotesHtml, "Mobile notes")
            : web.PutAsJsonAsync(WebUrl(seed), RouteRequest(dto, duration: 123));
        var first = Save(notesFirst);
        Task<HttpResponseMessage>? second = null;
        try
        {
            await WaitForLockAsync(probe, 0);
            second = Save(!notesFirst);
            await WaitForLockAsync(probe, 1);
            await transaction.CommitAsync();
            using var one = await first.WaitAsync(TimeSpan.FromSeconds(10));
            using var two = await second.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(HttpStatusCode.OK, one.StatusCode);
            Assert.True(two.StatusCode == HttpStatusCode.OK, await two.Content.ReadAsStringAsync());
            await using var verify = fixture.CreateContext();
            var stored = await ReadAsync(verify, seed);
            Assert.Equal("Mobile notes", stored.Notes);
            Assert.Equal(123, stored.EstimatedDuration!.Value.TotalMinutes);
            Assert.Equal(dto.WaypointPlaceIds, stored.Waypoints.Select(w => w.PlaceId));
            Assert.Equal(dto.WaypointRouteVertexIndices, stored.Waypoints.Select(w => w.RouteVertexIndex));
            output.WriteLine($"Two routed physical Segment waits; notesFirst={notesFirst}; both independent edits committed; no silent Notes overwrite.");
        }
        finally
        {
            await transaction.DisposeAsync();
            using var one = await first.WaitAsync(TimeSpan.FromSeconds(10));
            if (second != null) { using var two = await second.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
    }

    /// <summary>Notes serialize with #848's real deletion owner; changed Segment content invalidates destructive approval.</summary>
    [PostgresFact]
    public async Task QueuedRegionDeletion_RejectsApprovalAfterNotesCommit()
    {
        var graph = await RegionDeletionTestGraph.SeedAsync(fixture);
        var segmentId = graph.EndpointIds[0];
        await using var db = fixture.CreateContext();
        var probe = new SegmentProbe();
        await using var app = await StartAsync(db, probe);
        using var web = await WebAsync(app, graph.UserId);
        var regionUrl = $"/api/trips/{graph.TripId}/editor/regions/{graph.SourceId}";
        using var previewResponse = await web.PostAsJsonAsync(regionUrl + "/deletion-preview", new { places = "delete", areas = "keep" });
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<RegionDeletionPreviewDto>())!;
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Segments\" WHERE \"Id\" = {segmentId} FOR UPDATE");
        var notes = web.PatchAsJsonAsync($"/api/trips/{graph.TripId}/editor/segments/{segmentId}/conditional",
            new { expected = new { notesHtml = "Segment notes" }, changes = new { notesHtml = "Protected newer notes" } });
        Task<HttpResponseMessage>? deletion = null;
        try
        {
            await WaitForLockAsync(probe, 0);
            deletion = web.PostAsJsonAsync(regionUrl + "/deletion", new { places = "delete", areas = "keep", confirmationToken = preview.Confirmation.Token });
            await WaitForLockAsync(probe, 1);
            await transaction.CommitAsync();
            using var saved = await notes.WaitAsync(TimeSpan.FromSeconds(10));
            using var rejected = await deletion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            await using var verify = fixture.CreateContext();
            Assert.Equal("Protected newer notes", (await verify.Segments.FindAsync(segmentId))!.Notes);
            Assert.True(await verify.Regions.AnyAsync(r => r.Id == graph.SourceId));
            Assert.Equal(graph.PlaceIds.Length, await verify.Places.CountAsync(p => graph.PlaceIds.Contains(p.Id)));
            Assert.Equal(2, probe.Updates.Count);
            Assert.Empty(_warmup.Invocations);
            output.WriteLine("Observed Notes and #848 deletion Segment waits; compatible lock order; changed Notes invalidated content-bound deletion approval with zero deletion effects.");
        }
        finally
        {
            await transaction.DisposeAsync();
            using var saved = await notes.WaitAsync(TimeSpan.FromSeconds(10));
            if (deletion != null) { using var rejected = await deletion.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
    }

    /// <summary>Failure before commit and loss after an actual commit remain uncertain to the HTTP caller, without replay.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitFailureOrLostAcknowledgement_NeverReturnsFalseNoEffect(bool committed)
    {
        var seed = await SeedAsync();
        var before = await SnapshotAsync(seed);
        var probe = new SegmentProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, probe, new FailCommit(committed));
        using var bearer = Bearer(app, seed.UserId);
        using var response = await PatchAsync(bearer, seed, false, "original notes", "Attempted notes");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await using var verify = fixture.CreateContext();
        Assert.Equal(committed ? "Attempted notes" : "original notes", (await ReadAsync(verify, seed)).Notes);
        if (!committed) Assert.Equal(before, await SnapshotAsync(seed));
        Assert.Equal(2, probe.Updates.Count);
        Assert.Empty(_warmup.Invocations);
        output.WriteLine($"Commit boundary injection; committed={committed}; HTTP 500 stays unknown; no automatic retry.");
    }

    /// <summary>Cancellation after the Segment SQL update must roll back both fields and perform no retry.</summary>
    [PostgresFact]
    public async Task CancellationAfterNotesUpdate_RollsBack()
    {
        var seed = await SeedAsync();
        var before = await SnapshotAsync(seed);
        using var cancellation = new CancellationTokenSource();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, new CancelAfterNotes(cancellation));
        using var bearer = Bearer(app, seed.UserId);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bearer.PatchAsJsonAsync(PatchUrl(seed, false),
            new { expected = new { notes = "original notes" }, changes = new { notes = "Cancelled" } }, cancellation.Token));
        Assert.Equal(before, await SnapshotAsync(seed));
        Assert.Empty(_warmup.Invocations);
    }

    /// <summary>Injects only at the actual transaction commit boundary.</summary>
    private sealed class FailCommit(bool after) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData data,
            InterceptionResult result, CancellationToken cancellationToken = default) => after
                ? ValueTask.FromResult(result) : throw new IOException("Controlled precommit failure");
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken cancellationToken = default) =>
            after ? throw new IOException("Controlled lost commit acknowledgement") : Task.CompletedTask;
    }

    /// <summary>Cancels once PostgreSQL has performed the Notes update, before commit.</summary>
    private sealed class CancelAfterNotes(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData data, int result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE \"Segments\"")) cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Records SQL footprints and the real lock connection without logging private parameter values.</summary>
    private sealed class SegmentProbe(string table = "Segments") : DbCommandInterceptor
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
            if (command.CommandText.Contains($"public.\"{table}\"") && command.CommandText.Contains("FOR UPDATE"))
                LockProcesses.Enqueue(((NpgsqlConnection)command.Connection!).ProcessID);
            return ValueTask.FromResult(result);
        }
        private void Record(DbCommand command)
        {
            if (command.CommandText.StartsWith("UPDATE ")) Updates.Enqueue(command.CommandText);
        }
    }

    /// <summary>Requires an observed PostgreSQL blocker, never just dispatch or a timing delay.</summary>
    private async Task WaitForLockAsync(SegmentProbe probe, int index)
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
        Assert.Fail("The routed request did not reach a physical PostgreSQL lock wait.");
    }
}
