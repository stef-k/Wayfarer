using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Executes the successful replacement and confirmation portions of the #407 PostgreSQL contract.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class TripEditorSegmentAggregatePostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>A waypoint-bearing create commits one complete aggregate and returns a committed-state token.</summary>
    [PostgresFact]
    public async Task WaypointBearingCreate_CommitsCompleteAggregateOnceAndIssuesToken()
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync(includeSegment: false);
        await using var context = fixture.CreateContext();
        var outcome = await support.Service(context).CreateSegmentAsync(seed.TripId, seed.UserId,
            TripEditorSegmentMutationPostgresTestSupport.Body(seed, [seed.FirstWaypointId, seed.SecondWaypointId], [1, 2], null, customRoute: true),
            CancellationToken.None);

        Assert.Equal(EditorRegionMutationStatus.Success, outcome.Status);
        var dto = outcome.Result!.Data;
        Assert.False(string.IsNullOrWhiteSpace(dto.AggregateConcurrencyToken));
        var stored = await TripEditorSegmentMutationPostgresTestSupport.ReadAsync(context, dto.Id);
        Assert.Equal([seed.FirstWaypointId, seed.SecondWaypointId], stored.Waypoints.Select(item => item.PlaceId));
        Assert.Equal([1, 2], stored.Waypoints.Select(item => item.RouteVertexIndex));
        Assert.NotNull(stored.RouteGeometry);
        Assert.Equal("updated notes", stored.Notes);
    }

    /// <summary>A complete update replaces waypoints, indices, geometry, profile, measurement, provenance, and notes atomically.</summary>
    [PostgresFact]
    public async Task WaypointBearingUpdate_ReplacesCompleteAggregateAndRefreshesToken()
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync(customRoute: false);
        await using var context = fixture.CreateContext();
        var originalToken = await support.TokenAsync(context, seed);
        var outcome = await support.Service(context).UpdateSegmentAsync(seed.TripId, seed.SegmentId!.Value, seed.UserId,
            TripEditorSegmentMutationPostgresTestSupport.Body(seed, [seed.AlternateId], [1], originalToken,
                mode: seed.SecondProfileKey, customRoute: true,
                route: [(0, 0), (1.5, 1.5), (3, 3)]), null, CancellationToken.None);

        Assert.Equal(EditorRegionMutationStatus.Success, outcome.Status);
        Assert.NotEqual(originalToken, outcome.Result!.Data.AggregateConcurrencyToken);
        var stored = await TripEditorSegmentMutationPostgresTestSupport.ReadAsync(context, seed.SegmentId.Value);
        Assert.Equal([seed.AlternateId], stored.Waypoints.Select(item => item.PlaceId));
        Assert.Equal([1], stored.Waypoints.Select(item => item.RouteVertexIndex));
        Assert.Equal(seed.SecondProfileId, stored.TransportProfileId);
        Assert.Equal(EstimatedDurationSource.Manual, stored.EstimatedDurationSource);
        Assert.Equal(TimeSpan.FromMinutes(91), stored.EstimatedDuration);
        Assert.Equal("updated notes", stored.Notes);
    }

    /// <summary>Explicit empty arrays deliberately remove every waypoint from a fallback aggregate.</summary>
    [PostgresFact]
    public async Task EmptyArrays_RemoveAllWaypoints()
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync(customRoute: false);
        await using var context = fixture.CreateContext();
        var token = await support.TokenAsync(context, seed);
        var outcome = await support.Service(context).UpdateSegmentAsync(seed.TripId, seed.SegmentId!.Value, seed.UserId,
            TripEditorSegmentMutationPostgresTestSupport.Body(seed, [], [], token), null, CancellationToken.None);

        Assert.Equal(EditorRegionMutationStatus.Success, outcome.Status);
        Assert.Empty((await TripEditorSegmentMutationPostgresTestSupport.ReadAsync(context, seed.SegmentId.Value)).Waypoints);
    }

    /// <summary>Pure waypoint removal needs no warning, preserves geometry, anonymizes the vertex, and shifts later indices.</summary>
    [PostgresFact]
    public async Task PureWaypointRemoval_PreservesGeometryAndReindexesSurvivor()
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync();
        await using var context = fixture.CreateContext();
        var token = await support.TokenAsync(context, seed);
        var outcome = await support.Service(context).UpdateSegmentAsync(seed.TripId, seed.SegmentId!.Value, seed.UserId,
            TripEditorSegmentMutationPostgresTestSupport.Body(seed, [seed.SecondWaypointId], [2], token, customRoute: true),
            null, CancellationToken.None);

        Assert.Equal(EditorRegionMutationStatus.Success, outcome.Status);
        var stored = await TripEditorSegmentMutationPostgresTestSupport.ReadAsync(context, seed.SegmentId.Value);
        Assert.Equal(4, stored.RouteGeometry!.NumPoints);
        Assert.Equal(seed.SecondWaypointId, Assert.Single(stored.Waypoints).PlaceId);
        Assert.Equal(2, Assert.Single(stored.Waypoints).RouteVertexIndex);
        Assert.Equal(1, stored.RouteGeometry.GetCoordinateN(1).X);
    }

    /// <summary>Addition, substitution, reorder, and endpoint replacement all require route-clear confirmation.</summary>
    [PostgresTheory]
    [InlineData("addition")]
    [InlineData("substitution")]
    [InlineData("reorder")]
    [InlineData("endpoint")]
    public async Task DestructiveAnchorChanges_RequireConfirmation(string scenario)
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync();
        await using var context = fixture.CreateContext();
        var token = await support.TokenAsync(context, seed);
        var ids = scenario switch
        {
            "addition" => new[] { seed.FirstWaypointId, seed.AlternateId, seed.SecondWaypointId },
            "substitution" => new[] { seed.AlternateId, seed.SecondWaypointId },
            "reorder" => new[] { seed.SecondWaypointId, seed.FirstWaypointId },
            _ => new[] { seed.FirstWaypointId, seed.SecondWaypointId }
        };
        var from = scenario == "endpoint" ? seed.AlternateId : seed.FromId;
        var outcome = await support.Service(context).UpdateSegmentAsync(seed.TripId, seed.SegmentId!.Value, seed.UserId,
            TripEditorSegmentMutationPostgresTestSupport.Body(seed, ids, ids.Select(_ => (int?)null).ToArray(), token,
                fromId: from), null, CancellationToken.None);

        Assert.Equal(EditorRegionMutationStatus.Conflict, outcome.Status);
        Assert.Equal("segment-route-clear-confirmation-required", Assert.IsType<EditorSegmentConflictDto>(outcome.Conflict).Code);
        var stored = await TripEditorSegmentMutationPostgresTestSupport.ReadAsync(context, seed.SegmentId.Value);
        Assert.Equal([seed.FirstWaypointId, seed.SecondWaypointId], stored.Waypoints.Select(item => item.PlaceId));
        Assert.NotNull(stored.RouteGeometry);
    }
}
