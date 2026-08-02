using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Proves PostgreSQL row-lock serialization for complete Segment route proposals.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class SegmentRouteReconcilerConcurrencyPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Forces the declared proposal order and proves the later locked proposal wins without mixed rows.</summary>
    [PostgresTheory]
    [InlineData(ProposalShape.Nonzero, ProposalShape.Zero)]
    [InlineData(ProposalShape.Zero, ProposalShape.Nonzero)]
    [InlineData(ProposalShape.Forward, ProposalShape.Reverse)]
    public async Task SameSegment_ReconciliationsWaitThenCommitOneCompleteProposal(
        ProposalShape firstShape,
        ProposalShape secondShape)
    {
        var seeded = await SeedAsync(segmentCount: 1);
        var gate = new SaveGateInterceptor();
        await using var first = fixture.CreateContext(gate);
        await using var second = fixture.CreateContext();
        await first.Database.OpenConnectionAsync();
        await second.Database.OpenConnectionAsync();
        var secondPid = await BackendPidAsync(second);
        var firstProposal = Proposal(seeded, seeded.SegmentIds[0], firstShape);
        var secondProposal = Proposal(seeded, seeded.SegmentIds[0], secondShape);

        var firstTask = SegmentRouteReconciler.ReconcileAsync(first, firstProposal);
        await gate.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondTask = SegmentRouteReconciler.ReconcileAsync(second, secondProposal);
        await WaitUntilBlockedAsync(secondPid);
        gate.ReleaseSave.TrySetResult();
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.All(results, result => Assert.True(result.Succeeded));
        await AssertStoredProposalAsync(secondProposal);
    }

    /// <summary>Holding one Segment row lock does not block reconciliation of another Segment.</summary>
    [PostgresFact]
    public async Task DifferentSegments_AreNotGloballySerialized()
    {
        var seeded = await SeedAsync(segmentCount: 2);
        var gate = new SaveGateInterceptor();
        await using var first = fixture.CreateContext(gate);
        await using var second = fixture.CreateContext();
        var firstTask = SegmentRouteReconciler.ReconcileAsync(first,
            Proposal(seeded, seeded.SegmentIds[0], ProposalShape.Forward));
        await gate.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var secondResult = await SegmentRouteReconciler.ReconcileAsync(second,
            Proposal(seeded, seeded.SegmentIds[1], ProposalShape.Reverse)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(secondResult.Succeeded);
        gate.ReleaseSave.TrySetResult();
        Assert.True((await firstTask).Succeeded);
    }

    private async Task WaitUntilBlockedAsync(int backendPid)
    {
        await using var monitor = fixture.CreateContext();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = monitor.Database.GetDbConnection().CreateCommand();
            if (command.Connection!.State != System.Data.ConnectionState.Open)
                await command.Connection.OpenAsync();
            command.CommandText = "SELECT cardinality(pg_blocking_pids(@pid))";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "pid";
            parameter.Value = backendPid;
            command.Parameters.Add(parameter);
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) > 0) return;
            await Task.Yield();
        }
        throw new TimeoutException("The second reconciliation did not enter a PostgreSQL lock wait.");
    }

    private static async Task<int> BackendPidAsync(ApplicationDbContext context)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT pg_backend_pid()";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task AssertStoredProposalAsync(SegmentRouteProposal proposal)
    {
        await using var verification = fixture.CreateContext();
        var segment = await verification.Segments.AsNoTracking().SingleAsync(item => item.Id == proposal.SegmentId);
        var waypoints = await verification.Set<SegmentWaypoint>().AsNoTracking()
            .Where(item => item.SegmentId == proposal.SegmentId).OrderBy(item => item.Position).ToArrayAsync();
        Assert.Equal(proposal.FromPlaceId, segment.FromPlaceId);
        Assert.Equal(proposal.ToPlaceId, segment.ToPlaceId);
        Assert.Equal(proposal.Waypoints.Select(item => item.PlaceId), waypoints.Select(item => item.PlaceId));
        Assert.Equal(proposal.Waypoints.Select(item => item.Position), waypoints.Select(item => item.Position));
        Assert.Equal(proposal.Waypoints.Select(item => item.RouteVertexIndex), waypoints.Select(item => item.RouteVertexIndex));
        Assert.Equal(proposal.RouteGeometry?.AsText(), segment.RouteGeometry?.AsText());
    }

    private static SegmentRouteProposal Proposal(Seeded seeded, Guid segmentId, ProposalShape shape)
    {
        if (shape == ProposalShape.Zero) return new(segmentId, null, null, [], null);
        var ids = shape == ProposalShape.Reverse ? seeded.WaypointIds.Reverse().ToArray() : seeded.WaypointIds;
        var waypoints = ids.Select((id, index) => new SegmentWaypointProposal(id, index, index + 1)).ToArray();
        var coordinates = new[] { seeded.FromId }.Concat(ids).Append(seeded.ToId)
            .Select(id => seeded.Coordinates[id]).ToArray();
        return new(segmentId, seeded.FromId, seeded.ToId, waypoints, new LineString(coordinates) { SRID = 4326 });
    }

    private async Task<Seeded> SeedAsync(int segmentCount)
    {
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Concurrent waypoint fixture" };
        var region = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id, Name = "Route" };
        var places = Enumerable.Range(1, 6).Select(index => new Place
        {
            Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = user.Id,
            Name = $"Anchor {index}", Location = new Point(index, index) { SRID = 4326 }
        }).ToArray();
        foreach (var place in places) region.Places.Add(place);
        trip.Regions.Add(region);
        var segments = Enumerable.Range(0, segmentCount).Select(index => new Segment
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id, Mode = "walk", DisplayOrder = index
        }).ToArray();
        foreach (var segment in segments) trip.Segments.Add(segment);
        fixture.RegisterTrip(trip.Id);
        await using var context = fixture.CreateContext();
        context.Trips.Add(trip);
        await context.SaveChangesAsync();
        return new(segments.Select(item => item.Id).ToArray(), places[0].Id, places[^1].Id,
            places[1..^1].Select(item => item.Id).ToArray(), places.ToDictionary(item => item.Id, item => item.Location!.Coordinate));
    }

    /// <summary>Identifies the complete aggregate proposal used by one concurrent writer.</summary>
    public enum ProposalShape { Zero, Nonzero, Forward, Reverse }

    private sealed record Seeded(
        Guid[] SegmentIds,
        Guid FromId,
        Guid ToId,
        Guid[] WaypointIds,
        Dictionary<Guid, Coordinate> Coordinates);

    private sealed class SaveGateInterceptor : SaveChangesInterceptor
    {
        /// <summary>Signals that the first writer holds its lock and has reached final persistence.</summary>
        internal TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Releases the first writer without relying on elapsed time.</summary>
        internal TaskCompletionSource ReleaseSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            SaveEntered.TrySetResult();
            await ReleaseSave.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
}
