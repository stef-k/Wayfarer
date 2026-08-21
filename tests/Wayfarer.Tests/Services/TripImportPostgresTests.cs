using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>PostgreSQL-only proof for global KML tag reconciliation and import atomicity.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class TripImportPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Generic route titles remain descriptive text and never select a transport profile.</summary>
    [PostgresFact]
    public async Task GenericKmlRouteImport_LeavesTransportUnassigned()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        await using var context = fixture.CreateContext();
        var service = new TripImportService(context, NullLogger<TripImportService>.Instance, CreateReconciler(context));

        var tripId = await service.ImportWayfarerKmlAsync(
            ToStream(CreateGenericRouteKml("Ella to Kandy by TRAIN")), user.Id, TripImportMode.CreateNew);
        fixture.RegisterTrip(tripId);

        var segment = await context.Segments.AsNoTracking().SingleAsync(item => item.TripId == tripId);
        Assert.Equal(string.Empty, segment.Mode);
        Assert.Null(segment.TransportProfileId);
        Assert.Equal(EstimatedDurationSource.Automatic, segment.EstimatedDurationSource);
        Assert.NotNull(segment.EstimatedDistanceKm);
        Assert.Null(segment.EstimatedDuration);
    }

    /// <summary>Proves generic rollback clears failed state and a retry persists only final budgeted geometry and measurements.</summary>
    [PostgresFact]
    public async Task GenericGeometryBudget_RollsBackThenRecoversWithFinalMeasuredGeometry()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var rejectedTag = $"fixture-generic-rollback-{Guid.NewGuid():N}";
        await using var context = fixture.CreateContext();
        var service = new TripImportService(context, NullLogger<TripImportService>.Instance, CreateReconciler(context));

        await Assert.ThrowsAsync<TripImportValidationException>(() => service.ImportWayfarerKmlAsync(
            ToStream(CreateOversizedGenericRouteKml("walk", $"{rejectedTag}, ---")),
            user.Id,
            TripImportMode.CreateNew));

        Assert.Empty(context.ChangeTracker.Entries());
        await using (var failedVerification = fixture.CreateContext())
        {
            Assert.Empty(await failedVerification.Trips.Where(trip => trip.UserId == user.Id).ToListAsync());
            Assert.Empty(await failedVerification.Tags.Where(tag => tag.Slug == rejectedTag).ToListAsync());
        }

        var result = await service.ImportWayfarerKmlAsync(
            ToStream(CreateOversizedGenericRouteKml("walk", "")), user.Id, TripImportMode.CreateNew);
        fixture.RegisterTrip(result.TripId);

        await using var verification = fixture.CreateContext();
        var segment = await verification.Segments.AsNoTracking().Include(item => item.Waypoints)
            .SingleAsync(item => item.TripId == result.TripId);
        var geometry = Assert.IsType<LineString>(segment.RouteGeometry);
        Assert.InRange(geometry.NumPoints, 2, 500);
        Assert.Equal(0d, geometry.GetCoordinateN(0).X);
        Assert.Equal(0.2d, geometry.GetCoordinateN(geometry.NumPoints - 1).X);
        Assert.Empty(segment.Waypoints);
        Assert.Equal(string.Empty, segment.Mode);
        Assert.Null(segment.TransportProfileId);
        Assert.Equal(EstimatedDurationSource.Automatic, segment.EstimatedDurationSource);
        Assert.Null(segment.EstimatedDuration);
        Assert.NotNull(segment.EstimatedDistanceKm);
    }
    [PostgresFact]
    public async Task Tags_UseCitextAndBothGlobalUniqueIndexes()
    {
        fixture.RequireAvailable();
        var original = new Tag { Id = Guid.NewGuid(), Name = "Fixture Hike", Slug = $"fixture-hike-{Guid.NewGuid():N}" };
        fixture.RegisterTag(original);
        await using var context = fixture.CreateContext();
        context.Tags.Add(original);
        await context.SaveChangesAsync();

        Assert.Equal(original.Id, (await context.Tags.SingleAsync(tag => tag.Name == "fixture hike")).Id);

        context.Tags.Add(new Tag { Id = Guid.NewGuid(), Name = "FIXTURE HIKE", Slug = $"fixture-name-conflict-{Guid.NewGuid():N}" });
        var nameConflict = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Contains("IX_Tags_Name", nameConflict.GetBaseException().Message);
        context.ChangeTracker.Clear();

        context.Tags.Add(new Tag { Id = Guid.NewGuid(), Name = $"fixture-slug-conflict-{Guid.NewGuid():N}", Slug = original.Slug });
        var slugConflict = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Contains("IX_Tags_Slug", slugConflict.GetBaseException().Message);
    }

    [PostgresFact]
    public async Task ReconcileAsync_ReusesStoredNameCasing()
    {
        fixture.RequireAvailable();
        var existing = new Tag { Id = Guid.NewGuid(), Name = $"FixtureHike{Guid.NewGuid():N}", Slug = $"fixture-hike-{Guid.NewGuid():N}" };
        fixture.RegisterTag(existing);
        await using var context = fixture.CreateContext();
        context.Tags.Add(existing);
        await context.SaveChangesAsync();

        var result = await CreateReconciler(context).ReconcileAsync([existing.Slug.ToUpperInvariant()]);

        Assert.Equal(existing.Id, Assert.Single(result).Id);
        Assert.Equal(existing.Name, Assert.Single(result).Name);
    }

    [PostgresFact]
    public async Task ImportWayfarerKmlAsync_RollsBackGraphAndNewTagAfterInvalidToken()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        await using var context = fixture.CreateContext();
        var service = new TripImportService(context, NullLogger<TripImportService>.Instance, CreateReconciler(context));
        var tripId = Guid.NewGuid();

        await Assert.ThrowsAsync<TripImportValidationException>(() => service.ImportWayfarerKmlAsync(ToStream(CreateKml(tripId, $"fixture-new-{Guid.NewGuid():N}, ---")), user.Id));

        await using var verification = fixture.CreateContext();
        Assert.Empty(await verification.Trips.Where(trip => trip.UserId == user.Id).ToListAsync());
        Assert.Empty(await verification.Tags.Where(tag => tag.Slug.StartsWith("fixture-new-")).ToListAsync());
        Assert.Empty(await verification.Trips
            .Where(trip => trip.UserId == user.Id)
            .SelectMany(trip => trip.Tags)
            .ToListAsync());
    }

    [PostgresFact]
    public async Task ImportWayfarerKmlAsync_ConcurrentSameTagAttachesOneGlobalWinner()
    {
        fixture.RequireAvailable();
        var slug = $"fixture-race-{Guid.NewGuid():N}";
        var firstUser = await fixture.CreateUserAsync();
        var secondUser = await fixture.CreateUserAsync();
        var barrier = new TagInsertBarrier();
        await using var first = fixture.CreateContext(barrier);
        await using var second = fixture.CreateContext(barrier);
        var firstTask = new TripImportService(first, NullLogger<TripImportService>.Instance, CreateReconciler(first))
            .ImportWayfarerKmlAsync(ToStream(CreateKml(Guid.NewGuid(), slug)), firstUser.Id, TripImportMode.CreateNew);
        var secondTask = new TripImportService(second, NullLogger<TripImportService>.Instance, CreateReconciler(second))
            .ImportWayfarerKmlAsync(ToStream(CreateKml(Guid.NewGuid(), slug)), secondUser.Id, TripImportMode.CreateNew);

        var tripIds = await Task.WhenAll(firstTask, secondTask);
        fixture.RegisterTrip(tripIds[0]);
        fixture.RegisterTrip(tripIds[1]);

        await using var verification = fixture.CreateContext();
        var importedIds = tripIds.Select(result => result.TripId).ToArray();
        var trips = await verification.Trips.Include(trip => trip.Tags).Where(trip => importedIds.Contains(trip.Id)).ToListAsync();
        Assert.Equal(2, trips.Count);
        var winner = Assert.Single(await verification.Tags.Where(tag => tag.Slug == slug).ToListAsync());
        fixture.RegisterTag(winner);
        Assert.All(trips, trip => Assert.Equal(winner.Id, Assert.Single(trip.Tags).Id));
    }

    [PostgresFact]
    public async Task ReconcileAsync_DivergentSlugAndNameRowsFailWithoutCommit()
    {
        fixture.RequireAvailable();
        var slug = $"fixture-divergent-{Guid.NewGuid():N}";
        var bySlug = new Tag { Id = Guid.NewGuid(), Name = $"fixture-other-{Guid.NewGuid():N}", Slug = slug };
        var byName = new Tag { Id = Guid.NewGuid(), Name = slug, Slug = $"fixture-name-{Guid.NewGuid():N}" };
        fixture.RegisterTag(bySlug);
        fixture.RegisterTag(byName);
        await using var context = fixture.CreateContext();
        context.Tags.AddRange(bySlug, byName);
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<TripImportValidationException>(() => CreateReconciler(context).ReconcileAsync([slug]));
        await using var verification = fixture.CreateContext();
        Assert.Equal(2, await verification.Tags.CountAsync(tag => tag.Id == bySlug.Id || tag.Id == byName.Id));
    }

    [PostgresFact]
    public async Task RelationalSmoke_ExercisesImportModesRollbackAndPrivateSuggestions()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var existingTag = new Tag { Id = Guid.NewGuid(), Name = "Fixture Stored Casing", Slug = $"fixture-stored-{Guid.NewGuid():N}" };
        var existingTrip = CreateTrip(user.Id, false, existingTag);
        fixture.RegisterTag(existingTag);
        fixture.RegisterTrip(existingTrip.Id);
        await using (var seed = fixture.CreateContext())
        {
            seed.AddRange(existingTag, existingTrip);
            await seed.SaveChangesAsync();
        }

        var importedSlug = $"fixture-imported-{Guid.NewGuid():N}";
        await using (var duplicateContext = fixture.CreateContext())
        {
            var service = new TripImportService(duplicateContext, NullLogger<TripImportService>.Instance, CreateReconciler(duplicateContext));
            await Assert.ThrowsAsync<TripDuplicateException>(() => service.ImportWayfarerKmlAsync(ToStream(CreateKml(existingTrip.Id, existingTag.Slug)), user.Id, TripImportMode.Auto));
        }

        Guid cloneId;
        await using (var cloneContext = fixture.CreateContext())
        {
            var service = new TripImportService(cloneContext, NullLogger<TripImportService>.Instance, CreateReconciler(cloneContext));
            cloneId = await service.ImportWayfarerKmlAsync(
                ToStream(CreateKml(existingTrip.Id, $" {existingTag.Slug}, {importedSlug}, {existingTag.Slug.ToUpperInvariant()}, ,  ")),
                user.Id,
                TripImportMode.CreateNew);
            fixture.RegisterTrip(cloneId);

            var clone = await cloneContext.Trips.Include(trip => trip.Tags).SingleAsync(trip => trip.Id == cloneId);
            Assert.Equal(new[] { "Fixture Stored Casing", importedSlug }, clone.Tags.Select(tag => tag.Name).OrderBy(name => name));
            foreach (var tag in clone.Tags) fixture.RegisterTag(tag);
        }

        await using (var upsertContext = fixture.CreateContext())
        {
            var service = new TripImportService(upsertContext, NullLogger<TripImportService>.Instance, CreateReconciler(upsertContext));
            var upsertSlug = $"fixture-upsert-{Guid.NewGuid():N}";
            await service.ImportWayfarerKmlAsync(ToStream(CreateKml(existingTrip.Id, upsertSlug)), user.Id, TripImportMode.Upsert);
            var upserted = await upsertContext.Trips.Include(trip => trip.Tags).SingleAsync(trip => trip.Id == existingTrip.Id);
            Assert.Equal(new[] { upsertSlug }, upserted.Tags.Select(tag => tag.Slug));
            fixture.RegisterTag(upserted.Tags.Single());
        }

        await using (var failureContext = fixture.CreateContext())
        {
            var service = new TripImportService(failureContext, NullLogger<TripImportService>.Instance, CreateReconciler(failureContext));
            await Assert.ThrowsAsync<TripImportValidationException>(() => service.ImportWayfarerKmlAsync(
                ToStream(CreateKml(Guid.NewGuid(), $"fixture-rollback-{Guid.NewGuid():N}, ---")), user.Id, TripImportMode.CreateNew));
        }

        await using var verification = fixture.CreateContext();
        Assert.Equal(2, await verification.Trips.CountAsync(trip => trip.UserId == user.Id));
        Assert.Empty(await verification.Tags.Where(tag => tag.Slug.StartsWith("fixture-rollback-")).ToListAsync());

    }

    private static TripImportTagReconciler CreateReconciler(ApplicationDbContext context) => new(context, NullLogger<TripImportTagReconciler>.Instance);

    private static MemoryStream ToStream(string kml) => new(Encoding.UTF8.GetBytes(kml));

    /// <summary>Creates a tag-only versionless-v1 fixture with a native region identity.</summary>
    private static string CreateKml(Guid id, string tags) => $@"<kml xmlns=""http://www.opengis.net/kml/2.2""><Document><name>Trip</name><ExtendedData>
<Data name=""TripId""><value>{id}</value></Data><Data name=""Tags""><value>{tags}</value></Data></ExtendedData>
<Folder><name>Imported Region</name><ExtendedData><Data name=""RegionId""><value>{Guid.NewGuid()}</value></Data></ExtendedData></Folder>
</Document></kml>";

    private static string CreateGenericRouteKml(string mode) => $@"
<kml xmlns=""http://www.opengis.net/kml/2.2""><Document><name>Generic</name><Folder><name>Routes</name>
<Placemark><name>{mode}</name><LineString><coordinates>0,0 1,0</coordinates></LineString></Placemark>
</Folder></Document></kml>";

    private static string CreateOversizedGenericRouteKml(string mode, string tags)
    {
        var coordinates = string.Join(' ', Enumerable.Range(0, 2_001).Select(index =>
            $"{(index * 0.0001d).ToString("R", System.Globalization.CultureInfo.InvariantCulture)},40"));
        return $@"<kml xmlns=""http://www.opengis.net/kml/2.2"" xmlns:wf=""https://wayfarer.stefk.me/kml""><Document><name>Generic</name>
<ExtendedData><wf:Tags>{tags}</wf:Tags></ExtendedData><Placemark><name>{mode}</name>
<LineString><coordinates>{coordinates}</coordinates></LineString></Placemark></Document></kml>";
    }

    private static Trip CreateTrip(string userId, bool isPublic, params Tag[] tags) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Name = "Import fixture trip",
        IsPublic = isPublic,
        UpdatedAt = DateTime.UtcNow,
        Tags = tags.ToList(),
        Regions = [new Region { Id = Guid.NewGuid(), UserId = userId, Name = "Unassigned Places", Places = [] }]
    };

    /// <summary>Pauses both tag insert commands so the race reaches PostgreSQL deterministically.</summary>
    private sealed class TagInsertBarrier : DbCommandInterceptor
    {
        private readonly Barrier _barrier = new(2);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO \"Tags\"", StringComparison.Ordinal))
                _barrier.SignalAndWait(TimeSpan.FromSeconds(15));
            return ValueTask.FromResult(result);
        }
    }
}
