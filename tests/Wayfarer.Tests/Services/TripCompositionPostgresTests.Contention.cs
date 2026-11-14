using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

public sealed partial class TripCompositionPostgresTests
{
    /// <summary>Selected originals are checked after an actual row wait; the winner's disjoint/current data remains intact.</summary>
    [PostgresFact]
    public async Task SelectedFieldChangedDuringWaitRefusesWithoutRenewingDecision()
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var writer = fixture.CreateContext(); var created = await StartSelection(writer, a, b);
        var c = await Load(writer, created.TripId); var place = c.Regions.Single().Places.First();
        await using var held = await writer.Database.BeginTransactionAsync();
        await writer.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Places\" WHERE \"Id\" = {place.Id} FOR UPDATE");
        await using var applying = fixture.CreateContext(); await applying.Database.OpenConnectionAsync();
        var pid = ((Npgsql.NpgsqlConnection)applying.Database.GetDbConnection()).ProcessID;
        var task = Select(applying, a.UserId, created, "b", [new("place", b.Regions.Single().Places.First().Id, "reuse", DestinationId: place.Id, ExpectedParentId: place.RegionId,
            Fields: new(Notes: new("edit", E(place.Notes), E("Loser"))))]);
        try
        {
            await AssertBlocked(writer, pid);
            await writer.Places.Where(p => p.Id == place.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Notes, "Winner").SetProperty(p => p.MarkerColor, "disjoint"));
        }
        finally { await held.CommitAsync(); }
        await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => task);
        var saved = await writer.Places.AsNoTracking().SingleAsync(p => p.Id == place.Id);
        Assert.Equal("Winner", saved.Notes); Assert.Equal("disjoint", saved.MarkerColor);
        Assert.DoesNotContain("\"b\"", (await writer.Set<TripComposition>().AsNoTracking().SingleAsync(w => w.Id == created.CompositionId)).Mappings!);
    }

    /// <summary>Finish holds the shared account/workspace barriers before an apply can reach C; late mutations refuse closed state.</summary>
    [PostgresFact]
    public async Task FinishWinsAgainstWaitingSelectedAction()
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        TripConstructionResult created;
        await using (var setup = fixture.CreateContext()) created = await StartSelection(setup, a, b);
        var gate = new FinishSelectionGate();
        await using var finishing = fixture.CreateContext(gate);
        var finish = new TripCompositionService(finishing).FinishAsync(a.UserId, created.CompositionId!.Value);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var applying = fixture.CreateContext(); await applying.Database.OpenConnectionAsync();
        var pid = ((Npgsql.NpgsqlConnection)applying.Database.GetDbConnection()).ProcessID;
        var task = Select(applying, a.UserId, created, "b", [new("trip", b.Id, "reuse", DestinationId: created.TripId)]);
        await using var observe = fixture.CreateContext();
        try { await AssertBlocked(observe, pid); }
        finally { gate.Release.TrySetResult(); }
        await finish;
        await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => task);
        var workspace = await observe.Set<TripComposition>().AsNoTracking().SingleAsync(w => w.Id == created.CompositionId);
        Assert.NotNull(workspace.FinishedAt); Assert.Null(workspace.Mappings);
        Assert.True(await observe.Trips.AnyAsync(t => t.Id == created.TripId));
    }

    /// <summary>JSONB text normalization must not turn ordinary reads into writes, including after a real removal.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedReadsPersistOnlyRealRemovedDestinationChanges(bool removeDestination)
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var db = fixture.CreateContext(); var created = await StartSelection(db, a, b);
        var id = created.CompositionId!.Value;
        var before = await db.Set<TripComposition>().AsNoTracking().SingleAsync(w => w.Id == id);
        var beforeVersion = await MappingVersion(db, id);
        var mappings = JsonSerializer.Deserialize<List<TripCompositionMapping>>(before.Mappings!, TripCompositionSnapshot.Json)!;
        // PostgreSQL's JSONB text differs from the serializer even though the decisions are identical.
        var compact = JsonSerializer.Serialize(mappings, TripCompositionSnapshot.Json);
        Assert.NotEqual(before.Mappings, compact);
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(before.Mappings!).RootElement, JsonDocument.Parse(compact).RootElement));
        var removed = mappings.First(m => m.Kind == "area");
        if (removeDestination) await db.Areas.Where(p => p.Id == removed.DestinationId).ExecuteDeleteAsync();
        var first = E(await new TripCompositionService(db).ReadAsync(a.UserId, id));
        var after = await db.Set<TripComposition>().AsNoTracking().SingleAsync(w => w.Id == id);
        var afterVersion = await MappingVersion(db, id);
        var persisted = JsonSerializer.Deserialize<List<TripCompositionMapping>>(after.Mappings!, TripCompositionSnapshot.Json)!;
        Assert.Equal(mappings.Select(m => removeDestination && m == removed ? m with { Decision = "destinationRemoved" } : m), persisted);
        Assert.Equal(before.SnapshotA, after.SnapshotA); Assert.Equal(before.SnapshotB, after.SnapshotB);
        if (removeDestination) Assert.NotEqual(beforeVersion, afterVersion);
        else Assert.Equal(beforeVersion, afterVersion);
        for (var i = 0; i < 2; i++)
        {
            await using var fresh = fixture.CreateContext();
            Assert.True(JsonElement.DeepEquals(first, E(await new TripCompositionService(fresh).ReadAsync(a.UserId, id))));
            Assert.Equal(afterVersion, await MappingVersion(fresh, id));
        }
    }

    /// <summary>The waiting read has a real RepeatableRead snapshot before the first unchanged read commits.</summary>
    [PostgresFact]
    public async Task OverlappingUnchangedReadsBothReturnCoherentWorkspaceWithoutWrites()
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var observer = fixture.CreateContext(); var created = await StartSelection(observer, a, b);
        var id = created.CompositionId!.Value; var before = await MappingVersion(observer, id);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = fixture.CreateContext();
        var reading = new TripCompositionService(first).ReadAsync(a.UserId, id, credential: async token =>
        {
            entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10), token); return true;
        });
        await using var second = fixture.CreateContext(); await second.Database.OpenConnectionAsync();
        var pid = ((Npgsql.NpgsqlConnection)second.Database.GetDbConnection()).ProcessID;
        Task<object>? waiting = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            waiting = new TripCompositionService(second).ReadAsync(a.UserId, id);
            await AssertBlocked(observer, pid);
        }
        finally { release.TrySetResult(); }
        var results = await Task.WhenAll(reading, waiting!);
        Assert.True(JsonElement.DeepEquals(E(results[0]), E(results[1])));
        Assert.Equal(created.TripId, E(results[0]).GetProperty("tripId").GetGuid());
        Assert.Equal(before, await MappingVersion(observer, id));
    }

    /// <summary>Bearer revalidation after the account wait must see a credential revoked by the lock holder.</summary>
    [PostgresFact]
    public async Task ReadRevalidatesCredentialAfterAccountWait()
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var writer = fixture.CreateContext(); var created = await StartSelection(writer, a, b);
        writer.ApiTokens.Add(new ApiToken { UserId = a.UserId, User = await writer.Users.SingleAsync(u => u.Id == a.UserId), Name = "read-wait", Token = a.UserId });
        await writer.SaveChangesAsync();
        var before = await MappingVersion(writer, created.CompositionId!.Value);
        await using var held = await LocationDeletionFence.BeginAsync(writer, a.UserId, default);
        await using var reader = fixture.CreateContext(); await reader.Database.OpenConnectionAsync();
        var pid = ((Npgsql.NpgsqlConnection)reader.Database.GetDbConnection()).ProcessID;
        var reading = new TripCompositionService(reader).ReadAsync(a.UserId, created.CompositionId.Value, credential: async token =>
        {
            await using var authority = fixture.CreateContext();
            return await authority.ApiTokens.AnyAsync(t => t.Token == a.UserId, token);
        });
        try
        {
            await AssertBlocked(writer, pid);
            await writer.ApiTokens.Where(t => t.Token == a.UserId).ExecuteDeleteAsync();
        }
        finally { await held!.CommitAsync(); }
        var refusal = await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => reading);
        var response = Assert.IsType<ObjectResult>(refusal.ToActionResult());
        Assert.Equal(404, response.StatusCode);
        Assert.Equal("composition-unavailable", Assert.IsType<ProblemDetails>(response.Value).Extensions["code"]);
        Assert.Equal(before, await MappingVersion(writer, created.CompositionId.Value));
    }

    /// <summary>A physical tuple version detects UPDATEs even when PostgreSQL stores identical JSONB values.</summary>
    private static Task<string> MappingVersion(ApplicationDbContext db, Guid id) => db.Database.SqlQuery<string>($"""
        SELECT xmin::text AS "Value" FROM public."TripCompositions" WHERE "Id" = {id}
        """).SingleAsync();

    private static async Task AssertBlocked(ApplicationDbContext observer, int pid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await observer.Database.SqlQuery<bool>($"SELECT cardinality(pg_blocking_pids({pid})) > 0 AS \"Value\"").SingleAsync()) return;
            await Task.Delay(20);
        }
        Assert.Fail("The selected mutation did not reach a real database wait.");
    }
    private sealed class FinishSelectionGate : DbCommandInterceptor
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(System.Data.Common.DbCommand command,
            CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("Trips\"") && command.CommandText.Contains("FOR UPDATE"))
            { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
            return result;
        }
    }
}
