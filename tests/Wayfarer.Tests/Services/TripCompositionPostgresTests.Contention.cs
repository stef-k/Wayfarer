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
