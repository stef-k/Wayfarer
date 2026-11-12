using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Planning;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Batch 2 atomic construction and durable lifecycle at the real PostgreSQL/FK boundary.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class TripCompositionPostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OwnVersionPreservesIndependentGraphAndExplicitPlanningChoice(bool keepPlanning)
    {
        var a = await Seed();
        await using var db = fixture.CreateContext();
        var result = await new TripCompositionService(db).CreateAsync(a.UserId, a.Id, null, "Version", "Private change", keepPlanning);
        fixture.RegisterTrip(result.TripId);
        var c = await Load(db, result.TripId);
        Assert.Null(result.CompositionId);
        Assert.False(c.IsPublic); Assert.False(c.ShareProgressEnabled); Assert.Null(c.ContributionPolicy);
        Assert.Equal("Version", c.Name); Assert.Equal(a.Credits, c.Credits);
        Assert.DoesNotContain(c.Credits, credit => credit.SourceTitle == a.Name);
        Assert.Equal(a.CoverImageUrl, c.CoverImageUrl);
        Assert.Equal(a.Tags.Single().Id, c.Tags.Single().Id);
        Assert.NotEqual(a.Id, c.Id);
        Assert.Equal(keepPlanning, c.Planning != null);
        var region = c.Regions.Single();
        Assert.NotEqual(a.Regions.Single().Id, region.Id);
        Assert.Equal(a.Regions.Single().CoverImageUrl, region.CoverImageUrl);
        Assert.Equal(new[] { "Start", "Via", "End", "Unlocated" }, region.Places.OrderBy(p => p.DisplayOrder).Select(p => p.Name));
        Assert.Equal(new[] { "Alpha", "Zulu" }, region.Areas.OrderBy(p => p.DisplayOrder).Select(p => p.Name));
        Assert.All(region.Areas, area => Assert.Equal(1, area.Geometry.NumInteriorRings));
        var segment = c.Segments.Single();
        Assert.Equal(region.Places.Single(p => p.Name == "Start").Id, segment.FromPlaceId);
        Assert.Equal(region.Places.Single(p => p.Name == "End").Id, segment.ToPlaceId);
        Assert.Equal(region.Places.Single(p => p.Name == "Via").Id, segment.Waypoints.Single().PlaceId);
        Assert.Equal(1, segment.Waypoints.Single().RouteVertexIndex);
        Assert.Equal(a.Segments.Single().RouteGeometry!.AsText(), segment.RouteGeometry!.AsText());
        Assert.Equal(a.Segments.Single().EstimatedDuration, segment.EstimatedDuration);
        Assert.Equal(a.Segments.Single().EstimatedDistanceKm, segment.EstimatedDistanceKm);
        var targets = new IPlanningTarget[] { c, region, segment }.Concat(region.Places).Concat(region.Areas).ToArray();
        Assert.All(targets, target => Assert.Equal(keepPlanning, target.Planning != null));
        if (keepPlanning) Assert.All(targets, target => Assert.DoesNotContain(target.Planning!.Entries, e => e.Id == a.Planning!.Entries[0].Id));
        Assert.Equal("Private change", (await db.Set<TripOrigin>().SingleAsync(o => o.TripId == c.Id)).ChangeNote);
        Assert.Empty(await db.Set<TripComposition>().Where(w => w.TripId == c.Id).ToArrayAsync());
        var unchanged = await Load(db, a.Id);
        Assert.Equal(a.Planning!.Entries[0].Id, unchanged.Planning!.Entries[0].Id);
        Assert.Null(unchanged.Regions.Single().Places.Single(p => p.Name == "Unlocated").DisplayOrder);
    }

    [PostgresTheory]
    [InlineData("CC-BY-4.0", true, true, true)]
    [InlineData("CC0-1.0", true, true, true)]
    [InlineData(null, true, true, false)]
    [InlineData("view-only", true, true, false)]
    [InlineData("CC-BY-4.0", false, true, false)]
    [InlineData("CC-BY-4.0", true, false, false)]
    public async Task ForeignCaptureRequiresCurrentAuthorityAndNeverIncludesPlanning(string? policy, bool published, bool active, bool granted)
    {
        var a = await Seed(); var b = await Seed();
        await using var db = fixture.CreateContext();
        await db.Trips.Where(t => t.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ContributionPolicy, policy).SetProperty(t => t.IsPublic, published));
        await db.Users.Where(u => u.Id == b.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, active));
        var service = new TripCompositionService(db);
        if (!granted)
        {
            await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => service.CreateAsync(a.UserId, a.Id, b.Id, null, null));
            return;
        }
        var result = await service.CreateAsync(a.UserId, a.Id, b.Id, null, null);
        fixture.RegisterTrip(result.TripId);
        Assert.True(result.MediaOmitted);
        var workspace = await db.Set<TripComposition>().SingleAsync(w => w.Id == result.CompositionId);
        Assert.DoesNotContain("planning", workspace.SnapshotB!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(b.UserId, workspace.SnapshotB!);
        Assert.DoesNotContain("secret-plan", workspace.SnapshotB!);
        Assert.DoesNotContain("private-image", workspace.SnapshotB!);
        Assert.Contains("secret-plan", workspace.SnapshotA!);
        Assert.Contains("Retain this notice", workspace.SnapshotB!);
        var c = await Load(db, result.TripId);
        Assert.Equal(a.Credits, c.Credits); // B has not been adopted.
        Assert.Equal(a.Planning!.Entries[0].Label, c.Planning!.Entries[0].Label);
    }

    [PostgresFact]
    public async Task ResumeUsesSameCurrentDestinationAndFinishDropsOnlyComparisonPayload()
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        TripConstructionResult result;
        await using (var db = fixture.CreateContext())
        {
            result = await new TripCompositionService(db).CreateAsync(a.UserId, a.Id, b.Id, null, null);
            fixture.RegisterTrip(result.TripId);
        }
        await using var read = fixture.CreateContext();
        var service = new TripCompositionService(read);
        var before = await read.Set<TripComposition>().AsNoTracking().SingleAsync(w => w.Id == result.CompositionId);
        Assert.Contains("secret-plan", before.SnapshotB!);
        await read.Trips.Where(t => t.Id == result.TripId).ExecuteUpdateAsync(s => s.SetProperty(t => t.Name, "Edited C").SetProperty(t => t.IsPublic, true));
        var removed = await read.Areas.Where(p => p.Region.TripId == result.TripId).FirstAsync();
        read.Areas.Remove(removed); await read.SaveChangesAsync(); read.ChangeTracker.Clear();
        await read.Trips.Where(t => t.Id == a.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Name, "Changed source"));
        await read.Trips.Where(t => t.Id == b.Id).ExecuteDeleteAsync();
        var resumed = JsonSerializer.SerializeToElement(await service.ReadAsync(a.UserId, result.CompositionId!.Value));
        Assert.Equal(result.TripId, resumed.GetProperty("tripId").GetGuid());
        Assert.Contains("Edited C", resumed.GetProperty("current").ToString());
        Assert.Contains("destinationRemoved", resumed.GetProperty("mappings").ToString());
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(before.SnapshotA!).RootElement, resumed.GetProperty("sourceA")));
        Assert.Single((JsonSerializer.SerializeToElement(await service.ListAsync(a.UserId, a.Id, b.Id))).EnumerateArray());
        var another = await service.CreateAsync(a.UserId, a.Id, a.Id, null, null);
        fixture.RegisterTrip(another.TripId); Assert.NotEqual(result.TripId, another.TripId);
        var c = await Load(read, result.TripId);
        var timestamp = c.UpdatedAt;
        await service.FinishAsync(a.UserId, result.CompositionId.Value);
        var finished = await read.Set<TripComposition>().AsNoTracking().SingleAsync(w => w.Id == result.CompositionId);
        Assert.NotNull(finished.FinishedAt); Assert.Null(finished.SnapshotA); Assert.Null(finished.SnapshotB); Assert.Null(finished.Mappings);
        Assert.Null(finished.SourceAId); Assert.Null(finished.SourceBId);
        var after = await Load(read, c.Id);
        Assert.Equal("Edited C", after.Name); Assert.True(after.IsPublic); Assert.Equal(timestamp, after.UpdatedAt);
        Assert.Equal(c.Credits, after.Credits); Assert.Single(after.Regions.Single().Areas);
        Assert.True(await read.Set<TripOrigin>().AnyAsync(o => o.TripId == c.Id));
        await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => service.FinishAsync(a.UserId, finished.Id));
        await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => service.ReadAsync(a.UserId, finished.Id));
        Assert.True(await TripCompositionLifecycle.DeleteAsync(read, c.Id, a.UserId, default));
        Assert.False(await read.Set<TripComposition>().AnyAsync(w => w.TripId == c.Id));
        Assert.False(await read.Set<TripOrigin>().AnyAsync(o => o.TripId == c.Id));
        Assert.True(await read.Trips.AnyAsync(t => t.Id == a.Id));
        Assert.True(await read.Trips.AnyAsync(t => t.Id == another.TripId));
    }

    [PostgresFact]
    public async Task LateRouteFailureRollsBackDestinationCreditsWorkspaceAndOrigins()
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var db = fixture.CreateContext(new FailAfterRouteSave());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TripCompositionService(db).CreateAsync(a.UserId, a.Id, b.Id, null, null));
        await using var verify = fixture.CreateContext();
        Assert.Equal(2, await verify.Trips.CountAsync(t => t.UserId == a.UserId));
        Assert.False(await verify.Set<TripComposition>().AnyAsync(w => w.UserId == a.UserId));
        Assert.False(await verify.Set<TripOrigin>().AnyAsync(o => o.SourceTripId == a.Id));
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroViaRouteMustMatchPresentEndpoints(bool offset)
    {
        var a = await Seed();
        await using var db = fixture.CreateContext();
        var segment = await db.Segments.Include(s => s.Waypoints).SingleAsync(s => s.TripId == a.Id);
        db.RemoveRange(segment.Waypoints); segment.RouteGeometry = new LineString([new Coordinate(offset ? 12.1 : 12, 40), new Coordinate(14, 42)]) { SRID = 4326 };
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var service = new TripCompositionService(db);
        if (offset) await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => service.CreateAsync(a.UserId, a.Id, null, null, null));
        else
        {
            var copy = await service.CreateAsync(a.UserId, a.Id, null, null, null); fixture.RegisterTrip(copy.TripId);
            var saved = (await Load(db, copy.TripId)).Segments.Single();
            Assert.Empty(saved.Waypoints); Assert.Equal(segment.EstimatedDuration, saved.EstimatedDuration);
        }
    }

    /// <summary>Source account erasure retains independent captured content; destination erasure cascades private state.</summary>
    [PostgresFact]
    public async Task AccountErasureDoesNotOwnAnotherUsersDestination()
    {
        var a = await Seed(); var b = await Seed();
        await using var db = fixture.CreateContext();
        var result = await new TripCompositionService(db).CreateAsync(a.UserId, a.Id, b.Id, null, null);
        fixture.RegisterTrip(result.TripId);
        await db.Users.Where(u => u.Id == b.UserId).ExecuteDeleteAsync();
        Assert.Contains("Retain this notice", JsonSerializer.Serialize(await new TripCompositionService(db).ReadAsync(a.UserId, result.CompositionId!.Value)));
        await db.Users.Where(u => u.Id == a.UserId).ExecuteDeleteAsync();
        Assert.False(await db.Set<TripComposition>().AnyAsync(w => w.Id == result.CompositionId));
        Assert.False(await db.Set<TripOrigin>().AnyAsync(o => o.TripId == result.TripId));
    }

    /// <summary>A source grant changed after a coherent snapshot begins cannot authorize later capture.</summary>
    [PostgresFact]
    public async Task RevocationDuringAcquisitionRefusesWithoutPartialDestination()
    {
        var a = await Seed(); var b = await Seed();
        var gate = new SourceGate();
        await using var acquiring = fixture.CreateContext(gate);
        var attempt = new TripCompositionService(acquiring).CreateAsync(a.UserId, a.Id, b.Id, null, null);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var writer = fixture.CreateContext();
            await writer.Trips.Where(t => t.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ContributionPolicy, "view-only"));
        }
        finally { gate.Release.TrySetResult(); }
        await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => attempt);
        await using var verify = fixture.CreateContext();
        Assert.Equal(1, await verify.Trips.CountAsync(t => t.UserId == a.UserId));
    }

    /// <summary>A real account-lock wait lets deletion win; the late Finish refuses rather than recreating anything.</summary>
    [PostgresFact]
    public async Task DestinationDeletionWinsWhileFinishWaits()
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var writer = fixture.CreateContext();
        var created = await new TripCompositionService(writer).CreateAsync(a.UserId, a.Id, b.Id, null, null);
        fixture.RegisterTrip(created.TripId);
        await using var deletion = await LocationDeletionFence.BeginAsync(writer, a.UserId, default);
        await using var reader = fixture.CreateContext();
        await reader.Database.OpenConnectionAsync();
        var pid = ((Npgsql.NpgsqlConnection)reader.Database.GetDbConnection()).ProcessID;
        var finish = new TripCompositionService(reader).FinishAsync(a.UserId, created.CompositionId!.Value);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var blocked = false;
        while (!blocked && DateTime.UtcNow < deadline)
        {
            blocked = await writer.Database.SqlQuery<bool>($"SELECT cardinality(pg_blocking_pids({pid})) > 0 AS \"Value\"").SingleAsync();
            if (!blocked) await Task.Delay(20);
        }
        try
        {
            Assert.True(blocked, "Finish did not reach the real account barrier.");
            await TripCompositionLifecycle.LockAsync(writer, a.UserId, created.TripId, null, default);
            await writer.Trips.Where(t => t.Id == created.TripId).ExecuteDeleteAsync();
        }
        finally { await deletion!.CommitAsync(); }
        await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => finish);
        Assert.False(await writer.Set<TripComposition>().AnyAsync(w => w.Id == created.CompositionId));
    }

    /// <summary>Empty historical Trips remain empty, with no synthetic Region, route or planning value.</summary>
    [PostgresFact]
    public async Task EmptyOwnPrivateSourceCanBeVersioned()
    {
        var owner = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var a = new Trip { Id = Guid.NewGuid(), UserId = owner.Id, Name = "Empty" };
        fixture.RegisterTrip(a.Id); db.Trips.Add(a); await db.SaveChangesAsync();
        var created = await new TripCompositionService(db).CreateAsync(owner.Id, a.Id, null, null, null);
        fixture.RegisterTrip(created.TripId);
        var c = await Load(db, created.TripId);
        Assert.Empty(c.Regions); Assert.Empty(c.Segments); Assert.Null(c.Planning); Assert.Empty(c.Credits);
    }

    /// <summary>Ties, nullable child order and sparse fallback routes retain visible sequence after fresh identities.</summary>
    [PostgresFact]
    public async Task TiedListsAndHistoricalRoutesNormalizeOnlyDestination()
    {
        var a = await Seed();
        await using var db = fixture.CreateContext();
        await db.Places.Where(p => p.Region.TripId == a.Id && p.DisplayOrder != null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.DisplayOrder, 8));
        db.Regions.Add(new Region { Id = Guid.NewGuid(), TripId = a.Id, UserId = a.UserId, Name = "Unassigned Places", DisplayOrder = 0 });
        db.Segments.Add(new Segment { Id = Guid.NewGuid(), TripId = a.Id, UserId = a.UserId, Title = null, Mode = "walk", DisplayOrder = 0 });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var source = await Load(db, a.Id);
        var created = await new TripCompositionService(db).CreateAsync(a.UserId, a.Id, null, null, null);
        fixture.RegisterTrip(created.TripId);
        var c = await Load(db, created.TripId);
        Assert.Equal(source.Regions.OrderBy(r => r.DisplayOrder).ThenBy(r => r.Id).Select(r => r.Name), c.Regions.OrderBy(r => r.DisplayOrder).Select(r => r.Name));
        Assert.Equal(source.Segments.OrderBy(s => s.DisplayOrder).ThenBy(s => s.Id).Select(s => s.Title), c.Segments.OrderBy(s => s.DisplayOrder).Select(s => s.Title));
        var oldPlaces = source.Regions.Single(r => r.Name == "Region").Places;
        Assert.Equal(oldPlaces.OrderBy(p => p.DisplayOrder.HasValue ? 0 : 1).ThenBy(p => p.DisplayOrder).ThenBy(p => p.Id).Select(p => p.Name),
            c.Regions.Single(r => r.Name == "Region").Places.OrderBy(p => p.DisplayOrder).Select(p => p.Name));
        var historical = c.Segments.Single(s => s.Title == null);
        Assert.Null(historical.FromPlaceId); Assert.Null(historical.ToPlaceId); Assert.Null(historical.RouteGeometry);
        Assert.Null(historical.EstimatedDistanceKm); Assert.Null(historical.EstimatedDuration);
        Assert.All((await Load(db, a.Id)).Regions.Single(r => r.Name == "Region").Places.Where(p => p.DisplayOrder != null), p => Assert.Equal(8, p.DisplayOrder));
    }

    /// <summary>Pauses source acquisition at its actual row barrier after the repeatable snapshot has started.</summary>
    private sealed class SourceGate : DbCommandInterceptor
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR SHARE") && command.CommandText.Contains("Trips"))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    private async Task<Trip> Seed(string? owner = null)
    {
        owner ??= (await fixture.CreateUserAsync()).Id;
        var plan = new PlanningAggregate(1, [new(Guid.NewGuid(), new("unknown"), "secret-plan", "presence", 0)]);
        var trip = new Trip { Id = Guid.NewGuid(), UserId = owner, Name = "Private source", IsPublic = true,
            ContributionPolicy = "CC-BY-4.0", ShareProgressEnabled = true, Planning = plan,
            CoverImageUrl = "https://example.org/private-image.jpg", Credits = [new("Prior author", "Prior source", "Other terms", Notices: "Retain this notice")] };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, UserId = owner, Name = "Region", Planning = plan, CoverImageUrl = trip.CoverImageUrl };
        trip.Regions.Add(region);
        var places = new[] { "Start", "Via", "End", "Unlocated" }.Select((name, index) => new Place
        {
            Id = Guid.Parse($"00000000-0000-0000-0000-{Guid.NewGuid().ToString("N")[..10]}{index:00}"), RegionId = region.Id, UserId = owner,
            Name = name, DisplayOrder = index == 3 ? null : index, Location = index == 3 ? null : new Point(12 + index, 40 + index) { SRID = 4326 }, Planning = plan
        }).ToArray();
        foreach (var place in places) region.Places.Add(place);
        var shell = new LinearRing([new(1, 1), new(5, 1), new(5, 5), new(1, 5), new(1, 1)]);
        var hole = new LinearRing([new(2, 2), new(2, 3), new(3, 3), new(3, 2), new(2, 2)]);
        foreach (var name in new[] { "Zulu", "Alpha" }) region.Areas.Add(new Area { Id = Guid.NewGuid(), RegionId = region.Id, Name = name,
            DisplayOrder = 0, Geometry = new Polygon(shell, [hole]) { SRID = 4326 }, Planning = plan });
        var segment = new Segment { Id = Guid.NewGuid(), TripId = trip.Id, UserId = owner, FromPlaceId = places[0].Id, ToPlaceId = places[2].Id,
            Title = "Precise route", Mode = "walk", EstimatedDuration = TimeSpan.FromTicks(12345670), EstimatedDurationSource = EstimatedDurationSource.Manual,
            EstimatedDistanceKm = 1.23456789, Planning = plan,
            RouteGeometry = new LineString([new(12, 40), new(13, 41), new(14, 42)]) { SRID = 4326 } };
        segment.Waypoints.Add(new() { SegmentId = segment.Id, PlaceId = places[1].Id, Position = 0, RouteVertexIndex = 1 }); trip.Segments.Add(segment);
        var tag = new Tag { Id = Guid.NewGuid(), Name = "fixture-" + Guid.NewGuid().ToString("N"), Slug = "fixture-" + Guid.NewGuid().ToString("N") };
        trip.Tags.Add(tag); fixture.RegisterTag(tag); fixture.RegisterTrip(trip.Id);
        await using var db = fixture.CreateContext(); db.Trips.Add(trip); await db.SaveChangesAsync();
        return trip;
    }

    private static Task<Trip> Load(ApplicationDbContext db, Guid id) => db.Trips.AsNoTracking().Include(t => t.Tags)
        .Include(t => t.Regions).ThenInclude(r => r.Places).Include(t => t.Regions).ThenInclude(r => r.Areas)
        .Include(t => t.Segments).ThenInclude(s => s.Waypoints).AsSplitQuery().SingleAsync(t => t.Id == id);

    /// <summary>Fails after real route/dependency inserts, before the outer commit.</summary>
    private sealed class FailAfterRouteSave : SaveChangesInterceptor
    {
        private int saves;
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (++saves == 2) throw new InvalidOperationException("Injected late construction failure");
            return ValueTask.FromResult(result);
        }
    }
}
