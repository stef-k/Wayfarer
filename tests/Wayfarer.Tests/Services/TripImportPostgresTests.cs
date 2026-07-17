using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>PostgreSQL-only proof for global KML tag reconciliation and import atomicity.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class TripImportPostgresTests(PostgresImportTestFixture fixture)
{
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
        Assert.Equal(0, await verification.Set<Dictionary<string, object>>("TripTags").CountAsync());
    }

    [PostgresFact]
    public async Task ReconcileAsync_ConcurrentSameTagAttachesOneGlobalWinner()
    {
        fixture.RequireAvailable();
        var slug = $"fixture-race-{Guid.NewGuid():N}";
        var barrier = new TagInsertBarrier();
        await using var first = fixture.CreateContext(barrier);
        await using var second = fixture.CreateContext(barrier);
        var firstTask = CreateReconciler(first).ReconcileAsync([slug]);
        var secondTask = CreateReconciler(second).ReconcileAsync([slug]);

        var results = await Task.WhenAll(firstTask, secondTask);
        fixture.RegisterTag(Assert.Single(results[0]));

        Assert.Equal(results[0].Single().Id, results[1].Single().Id);
        await using var verification = fixture.CreateContext();
        Assert.Equal(1, await verification.Tags.CountAsync(tag => tag.Slug == slug));
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

    private static TripImportTagReconciler CreateReconciler(ApplicationDbContext context) => new(context, NullLogger<TripImportTagReconciler>.Instance);

    private static MemoryStream ToStream(string kml) => new(Encoding.UTF8.GetBytes(kml));

    private static string CreateKml(Guid id, string tags) => $@"<kml xmlns=""http://www.opengis.net/kml/2.2""><Document><name>Trip</name><ExtendedData><Data name=""TripId""><value>{id}</value></Data><Data name=""Tags""><value>{tags}</value></Data></ExtendedData></Document></kml>";

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
