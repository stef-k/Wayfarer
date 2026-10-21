using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class RegionConditionalPostgresTests
{
    /// <summary>Fresh Region values, parent scope and authority are decisive after an observed final row-lock wait.</summary>
    [PostgresTheory]
    [InlineData(false, "name", 409)]
    [InlineData(true, "name", 409)]
    [InlineData(false, "notes", 409)]
    [InlineData(true, "notes", 409)]
    [InlineData(false, "token", 401)]
    [InlineData(false, "inactive", 401)]
    [InlineData(true, "inactive", 401)]
    [InlineData(false, "region-owner", 404)]
    [InlineData(true, "trip-owner", 404)]
    [InlineData(true, "parent", 409)]
    public async Task LockWait_RevalidatesCurrentRegionAndAuthority(bool webRoute, string drift, int expectedStatus)
    {
        var region = await SeedAsync();
        var other = await SeedAsync();
        var probe = new RegionProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var client = webRoute ? await WebAsync(app, region.UserId) : Bearer(app, region.UserId);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Regions\" WHERE \"Id\" = {region.Id} FOR UPDATE");
        var key = drift == "notes" ? webRoute ? "notesHtml" : "notes" : "name";
        var pending = PatchAsync(client, region, webRoute,
            new Dictionary<string, object?> { [key] = drift == "notes" ? region.Notes : region.Name },
            new Dictionary<string, object?> { [key] = "Must not commit" });
        try
        {
            await probe.LockStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForBlockAsync(probe.ProcessId);
            if (drift == "name") await blocker.Regions.Where(r => r.Id == region.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Name, "Changed during wait"));
            if (drift == "notes") await blocker.Regions.Where(r => r.Id == region.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Notes, "Changed during wait"));
            if (drift == "region-owner") await blocker.Regions.Where(r => r.Id == region.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.UserId, other.UserId));
            if (drift == "trip-owner") await blocker.Trips.Where(t => t.Id == region.TripId).ExecuteUpdateAsync(s => s.SetProperty(t => t.UserId, other.UserId));
            if (drift == "parent") await blocker.Regions.Where(r => r.Id == region.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.TripId, other.TripId));
            await using var authority = fixture.CreateContext();
            if (drift == "inactive") await authority.Users.Where(u => u.Id == region.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
            if (drift == "token") await authority.ApiTokens.Where(t => t.UserId == region.UserId).ExecuteDeleteAsync();
            await transaction.CommitAsync();
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(expectedStatus, (int)response.StatusCode);
            await using var verification = fixture.CreateContext();
            var stored = await verification.Regions.SingleAsync(r => r.Id == region.Id);
            Assert.Equal(drift == "name" ? "Changed during wait" : region.Name, stored.Name);
            Assert.Equal(drift == "notes" ? "Changed during wait" : region.Notes, stored.Notes);
            Assert.Equal(region.Trip.UpdatedAt, (await verification.Trips.FindAsync(region.TripId))!.UpdatedAt);
            Assert.Empty(probe.Updates);
            Assert.Empty(_warmup.Invocations);
            output.WriteLine($"Observed PostgreSQL Region blocker; web={webRoute}; drift={drift}; rejected {expectedStatus}; zero operation SQL updates.");
        }
        finally
        {
            await transaction.DisposeAsync();
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>Disjoint edits committed during the final Region lock wait remain intact with a property-only SQL footprint.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LockWait_DisjointFieldsSurvive(bool web)
    {
        var region = await SeedAsync();
        var probe = new RegionProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var client = web ? await WebAsync(app, region.UserId) : Bearer(app, region.UserId);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Regions\" WHERE \"Id\" = {region.Id} FOR UPDATE");
        var key = web ? "notesHtml" : "notes";
        var pending = PatchAsync(client, region, web,
            new Dictionary<string, object?> { [key] = region.Notes }, new Dictionary<string, object?> { [key] = "Requested notes" });
        try
        {
            await probe.LockStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForBlockAsync(probe.ProcessId);
            await blocker.Regions.Where(r => r.Id == region.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Name, "Concurrent name")
                .SetProperty(r => r.CoverImageUrl, "https://example.test/concurrent.jpg").SetProperty(r => r.Center, new NetTopologySuite.Geometries.Point(4, 3) { SRID = 4326 }));
            await transaction.CommitAsync();
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await using var verification = fixture.CreateContext();
            var stored = await verification.Regions.SingleAsync(r => r.Id == region.Id);
            Assert.Equal(("Concurrent name", "Requested notes", "https://example.test/concurrent.jpg", 4d, 3d, 1),
                (stored.Name, stored.Notes, stored.CoverImageUrl, stored.Center!.X, stored.Center.Y, stored.DisplayOrder));
            Assert.Equal(2, probe.Updates.Count);
            var sql = probe.Updates.Single(s => s.Contains("UPDATE \"Regions\""));
            Assert.Contains("\"Notes\" =", sql);
            Assert.DoesNotContain("\"Name\" =", sql);
            Assert.DoesNotContain("\"Center\" =", sql);
            Assert.DoesNotContain("\"CoverImageUrl\" =", sql);
            output.WriteLine(sql);
        }
        finally { await transaction.DisposeAsync(); using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    /// <summary>Failure after Region and Trip SQL rolls back both fields and stamp before any image work.</summary>
    [PostgresFact]
    public async Task FailedCommit_RollsBackScalarAndTripStamp()
    {
        var region = await SeedAsync();
        var probe = new RegionProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe, new FailRegionCommit()]);
        using var bearer = Bearer(app, region.UserId);
        using var response = await PatchAsync(bearer, region, false,
            new { name = region.Name, notes = region.Notes }, new { name = "Uncommitted", notes = "<img src='https://example.test/image.jpg'>" });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await using var verification = fixture.CreateContext();
        var stored = await verification.Regions.SingleAsync(r => r.Id == region.Id);
        Assert.Equal((region.Name, region.Notes), (stored.Name, stored.Notes));
        Assert.Equal(region.Trip.UpdatedAt, (await verification.Trips.FindAsync(region.TripId))!.UpdatedAt);
        Assert.Equal(2, probe.Updates.Count);
        Assert.Empty(_warmup.Invocations);
    }

    /// <summary>Cancellation during the final row wait never becomes a definitive no-effect conflict receipt.</summary>
    [PostgresFact]
    public async Task CancelledRegionWait_DoesNotWriteOrRetry()
    {
        var region = await SeedAsync();
        var probe = new RegionProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var bearer = Bearer(app, region.UserId);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Regions\" WHERE \"Id\" = {region.Id} FOR UPDATE");
        using var cancellation = new CancellationTokenSource();
        var pending = bearer.PatchAsJsonAsync($"/api/trips/{region.TripId}/conditional/regions/{region.Id}",
            new { expected = new { name = region.Name }, changes = new { name = "Cancelled" } }, cancellation.Token);
        await probe.LockStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForBlockAsync(probe.ProcessId);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await transaction.RollbackAsync();
        await using var verification = fixture.CreateContext();
        Assert.Equal(region.Name, (await verification.Regions.FindAsync(region.Id))!.Name);
        Assert.Empty(probe.Updates);
        Assert.Empty(_warmup.Invocations);
    }

    /// <summary>Injects a transaction failure at the existing EF seam, after domain writes but before commit.</summary>
    private sealed class FailRegionCommit : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData data,
            InterceptionResult result, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Controlled Region commit failure");
    }

    /// <summary>Observes actual SQL and identifies the final Region lock independently of the Trip prelock.</summary>
    private sealed class RegionProbe : DbCommandInterceptor
    {
        internal TaskCompletionSource LockStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ProcessId { get; private set; }
        internal List<string> Updates { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            if (command.CommandText.Contains("\"Regions\"") && command.CommandText.Contains("FOR UPDATE"))
            {
                ProcessId = ((NpgsqlConnection)command.Connection!).ProcessID;
                LockStarted.TrySetResult();
            }
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        /// <summary>Captures property footprints without sensitive parameter values.</summary>
        private void Record(DbCommand command)
        {
            if (command.CommandText.StartsWith("UPDATE ")) Updates.Add(command.CommandText);
        }
    }

    /// <summary>Confirms a PostgreSQL blocker rather than relying on command-start timing.</summary>
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
}
