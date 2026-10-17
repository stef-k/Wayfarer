using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Exercises deliberate changes to the one accepted measurement set at its persisted production owners.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class SegmentMeasurementAuthorityPostgresTests(PostgresImportTestFixture fixture)
{
    private readonly TripEditorSegmentMutationPostgresTestSupport _support = new(fixture);

    /// <summary>Unavailable provider configuration and planning-speed changes cannot replace a saved provider result.</summary>
    [PostgresFact]
    public async Task MetadataAndProfileEdits_RetainProviderSetAndKeepManualIntentSeparate()
    {
        var seed = await SeedAsync();
        var original = await ReadAsync(seed);
        // No personal provider configuration exists: ordinary Save must use the accepted persisted route.
        await SaveAsync(seed);
        AssertSameActiveState(original, await ReadAsync(seed));
        foreach (var speed in new double?[] { 10, null })
        {
            await using var context = fixture.CreateContext();
            var changed = await TransportProfileMeasurementReconciler.ReconcileAsync(
                context, seed.FirstProfileId, speed, seed.UserId, CancellationToken.None);
            Assert.True(changed.Succeeded);
            AssertSameActiveState(original, await ReadAsync(seed));
        }
        await SaveAsync(seed, EstimatedDurationSource.Manual, 1.5125);
        var manual = await ReadAsync(seed);
        Assert.Equal(TimeSpan.FromSeconds(91), manual.EstimatedDuration);
        Assert.Equal(EstimatedDurationSource.Manual, manual.EstimatedDurationSource);
        Assert.Equal(original.EstimatedDistanceKm, manual.EstimatedDistanceKm);
        Assert.Equal(SegmentRouteProvenance.Capture(original), SegmentRouteProvenance.Capture(manual));
        // A single active set has no hidden old automatic duration: regenerate to obtain a fresh provider estimate.
        await SaveAsync(seed);
        var automatic = await ReadAsync(seed);
        Assert.Null(automatic.EstimatedDuration);
        Assert.Equal(EstimatedDurationSource.Automatic, automatic.EstimatedDurationSource);
        Assert.Equal(original.EstimatedDistanceKm, automatic.EstimatedDistanceKm);
        Assert.Equal(SegmentRouteProvenance.Capture(original), SegmentRouteProvenance.Capture(automatic));
    }

    /// <summary>Geometry replacement, waypoint changes and Clear Route establish Wayfarer measurements and clear old claims.</summary>
    [PostgresTheory]
    [InlineData("geometry")]
    [InlineData("waypoint")]
    [InlineData("clear")]
    public async Task DeliberateRouteChanges_ReplaceMeasurementsAndProvenanceTogether(string change)
    {
        var seed = await SeedAsync();
        await SaveAsync(seed, change: change);
        await using var context = fixture.CreateContext();
        var saved = await context.Segments.Include(item => item.FromPlace).Include(item => item.ToPlace)
            .Include(item => item.Waypoints).ThenInclude(item => item.Place).SingleAsync(item => item.Id == seed.SegmentId);
        var coordinates = saved.RouteGeometry?.Coordinates ?? new[] { saved.FromPlace!.Location!.Coordinate }
            .Concat(saved.Waypoints.OrderBy(item => item.Position).Select(item => item.Place.Location!.Coordinate))
            .Append(saved.ToPlace!.Location!.Coordinate).ToArray();
        var expected = SegmentMeasurementCalculator.CalculateDistance(coordinates);
        Assert.Equal(expected.RoundedKilometres, saved.EstimatedDistanceKm);
        Assert.Equal(SegmentMeasurementCalculator.CalculateAutomaticDuration(expected.UnroundedMetres, 5), saved.EstimatedDuration);
        Assert.Null(SegmentRouteProvenance.Capture(saved));
        Assert.Equal(change == "clear", saved.RouteGeometry == null);
    }

    /// <summary>Both lifecycle measurement branches clear changed route provenance while retaining exact Manual duration.</summary>
    [PostgresTheory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task PlaceCoordinateChange_ReplacesProviderMeasurementsWithoutChangingManualValue(bool waypoints, bool manual)
    {
        var seed = await SeedAsync(manual, waypoints);
        await using (var context = fixture.CreateContext())
        {
            var place = await context.Places.AsNoTracking().SingleAsync(item => item.Id == seed.FromId);
            var service = new PlaceRegionLifecycleService(context,
                new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider()));
            var changed = await service.UpdatePlaceAsync(seed.TripId, place.Id, seed.UserId,
                new(place.RegionId, place.Name, place.Notes ?? "", place.Address ?? "", place.IconName ?? "",
                    place.MarkerColor ?? "", new Point(0.2, 0) { SRID = 4326 }), CancellationToken.None);
            Assert.True(changed.Succeeded);
        }
        var saved = await ReadAsync(seed);
        var expected = SegmentMeasurementCalculator.CalculateDistance(saved.RouteGeometry!.Coordinates);
        Assert.Null(SegmentRouteProvenance.Capture(saved));
        Assert.Equal(expected.RoundedKilometres, saved.EstimatedDistanceKm);
        Assert.Equal(manual ? TimeSpan.FromSeconds(360.25)
            : SegmentMeasurementCalculator.CalculateAutomaticDuration(expected.UnroundedMetres, 5), saved.EstimatedDuration);
    }

    /// <summary>Public-copy cloning preserves provider precision, source and either duration intent.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clone_PreservesOneAcceptedProviderSet(bool manual)
    {
        var seed = await SeedAsync(manual);
        var original = await ReadAsync(seed);
        var destination = await fixture.CreateUserAsync();
        Guid cloned;
        await using (var context = fixture.CreateContext())
        {
            await context.Trips.Where(item => item.Id == seed.TripId)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.IsPublic, true));
            var result = await new TripCloneCoordinator(context).CloneAsync(seed.TripId, destination.Id);
            Assert.Equal(TripCloneStatus.Succeeded, result.Status);
            cloned = result.ClonedTripId!.Value;
            fixture.RegisterTrip(cloned);
        }
        await using var verify = fixture.CreateContext();
        AssertSameActiveState(original, await verify.Segments.AsNoTracking().SingleAsync(item => item.TripId == cloned));
    }

    /// <summary>Seeds the normalized persistent-provider output already proven through real proposal acceptance elsewhere.</summary>
    private async Task<SegmentSeed> SeedAsync(bool manual = false, bool waypoints = true)
    {
        var seed = await _support.SeedAsync();
        await using var context = fixture.CreateContext();
        var segment = await context.Segments.Include(item => item.Waypoints).SingleAsync(item => item.Id == seed.SegmentId);
        if (!waypoints) context.RemoveRange(segment.Waypoints);
        segment.EstimatedDistanceKm = 1.251234;
        segment.EstimatedDuration = TimeSpan.FromSeconds(360.25);
        segment.EstimatedDurationSource = manual ? EstimatedDurationSource.Manual : EstimatedDurationSource.Automatic;
        new SegmentRouteProvenance("geoapify", "persistent", "drive", DateTimeOffset.UtcNow,
            "Powered by Geoapify|© OpenStreetMap contributors", "[]", seed.FirstProfileId, null, null).ApplyTo(segment);
        await context.SaveChangesAsync();
        return seed;
    }

    /// <summary>Submits ordinary editor JSON, retaining the real confirmation boundary for a deliberate route clear.</summary>
    private async Task SaveAsync(SegmentSeed seed, EstimatedDurationSource source = EstimatedDurationSource.Automatic,
        double? minutes = null, string? change = null)
    {
        await using var context = fixture.CreateContext();
        var ids = change == "waypoint" ? new[] { seed.SecondWaypointId } : [seed.FirstWaypointId, seed.SecondWaypointId];
        int?[] indices = change switch { "clear" => [null, null], "waypoint" => [2], "geometry" => [2, 3], _ => [1, 2] };
        using var baseline = TripEditorSegmentMutationPostgresTestSupport.Body(seed, ids, indices,
            await _support.TokenAsync(context, seed), customRoute: change != "clear",
            route: change == "geometry" ? [(0, 0), (0.5, 0.4), (1, 1), (2, 2), (3, 3)] : null);
        var request = (await JsonNode.ParseAsync(baseline))!;
        request["estimatedDurationSource"] = source.ToString();
        request["estimatedDurationMinutes"] = minutes;
        var payload = JsonSerializer.SerializeToUtf8Bytes(request);
        var service = _support.Service(context);
        using var body = new MemoryStream(payload);
        var saved = await service.UpdateSegmentAsync(seed.TripId, seed.SegmentId!.Value, seed.UserId, body, null, CancellationToken.None);
        if (saved.Conflict is EditorSegmentConflictDto { ConfirmationToken: { } confirmation })
        {
            using var confirmed = new MemoryStream(payload);
            saved = await service.UpdateSegmentAsync(seed.TripId, seed.SegmentId.Value, seed.UserId, confirmed, confirmation, CancellationToken.None);
        }
        Assert.Equal(EditorRegionMutationStatus.Success, saved.Status);
    }

    /// <summary>Reads measurements from a new provider context, never from mutation tracking state.</summary>
    private async Task<Segment> ReadAsync(SegmentSeed seed)
    {
        await using var context = fixture.CreateContext();
        return await context.Segments.AsNoTracking().SingleAsync(item => item.Id == seed.SegmentId);
    }

    /// <summary>Compares the single active numeric set and all retained provider metadata.</summary>
    private static void AssertSameActiveState(Segment expected, Segment actual)
    {
        Assert.Equal(expected.EstimatedDistanceKm, actual.EstimatedDistanceKm);
        Assert.Equal(expected.EstimatedDuration, actual.EstimatedDuration);
        Assert.Equal(expected.EstimatedDurationSource, actual.EstimatedDurationSource);
        Assert.Equal(SegmentRouteProvenance.Capture(expected), SegmentRouteProvenance.Capture(actual));
    }
}
