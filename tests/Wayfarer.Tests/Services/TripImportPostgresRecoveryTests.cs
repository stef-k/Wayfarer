using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Forces PostgreSQL tag conflicts after initial reconciliation lookups and verifies safe recovery.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class TripImportPostgresRecoveryTests(PostgresImportTestFixture fixture)
{
    [PostgresTheory]
    [InlineData(ConflictKey.Slug)]
    [InlineData(ConflictKey.Name)]
    public async Task ImportWayfarerKmlAsync_RecognizedConflictRequeriesBothKeysAndAttachesWinner(ConflictKey conflictKey)
    {
        fixture.RequireAvailable();
        using var logs = new TestLogProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var user = await fixture.CreateUserAsync();
        var slug = $"fixture-recovery-{Guid.NewGuid():N}";
        var winner = conflictKey == ConflictKey.Slug
            ? new Tag { Id = Guid.NewGuid(), Name = $"fixture-other-{Guid.NewGuid():N}", Slug = slug }
            : new Tag { Id = Guid.NewGuid(), Name = slug, Slug = $"fixture-other-{Guid.NewGuid():N}" };
        fixture.RegisterTag(winner);

        var interceptor = new ConflictRecoveryInterceptor(
            async () =>
            {
                await using var seed = fixture.CreateContext();
                seed.Tags.Add(winner);
                await seed.SaveChangesAsync();
            },
            async () =>
            {
                await using var repair = fixture.CreateContext();
                var persistedWinner = await repair.Tags.SingleAsync(tag => tag.Id == winner.Id);
                if (conflictKey == ConflictKey.Slug) persistedWinner.Name = slug;
                else persistedWinner.Slug = slug;
                await repair.SaveChangesAsync();
            });

        await using var context = fixture.CreateContext(interceptor);
        var service = new TripImportService(context, loggerFactory.CreateLogger<TripImportService>(), CreateReconciler(context, loggerFactory));

        var tripId = await service.ImportWayfarerKmlAsync(ToStream(CreateKml(Guid.NewGuid(), slug)), user.Id, TripImportMode.CreateNew);
        fixture.RegisterTrip(tripId);

        Assert.Equal(4, interceptor.TagLookupCount);
        await using var verification = fixture.CreateContext();
        var trip = await verification.Trips.Include(candidate => candidate.Tags).SingleAsync(candidate => candidate.Id == tripId);
        Assert.Equal(winner.Id, Assert.Single(trip.Tags).Id);
        Assert.Equal(slug, Assert.Single(trip.Tags).Name);
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Information
            && entry.Category == typeof(TripImportTagReconciler).FullName
            && entry.Message.Contains("Recognized concurrent KML import tag create", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task ImportWayfarerKmlAsync_PostConflictDivergentRowsFailAndRollBackCompleteGraph()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var slug = $"fixture-divergent-recovery-{Guid.NewGuid():N}";
        var bySlug = new Tag { Id = Guid.NewGuid(), Name = $"fixture-other-{Guid.NewGuid():N}", Slug = slug };
        var byName = new Tag { Id = Guid.NewGuid(), Name = slug, Slug = $"fixture-name-{Guid.NewGuid():N}" };
        fixture.RegisterTag(bySlug);
        fixture.RegisterTag(byName);

        var interceptor = new ConflictRecoveryInterceptor(
            async () =>
            {
                await using var seed = fixture.CreateContext();
                seed.Tags.Add(bySlug);
                await seed.SaveChangesAsync();
            },
            async () =>
            {
                await using var seed = fixture.CreateContext();
                seed.Tags.Add(byName);
                await seed.SaveChangesAsync();
            },
            repairAfterLookup: 3);

        await using var context = fixture.CreateContext(interceptor);
        var service = new TripImportService(context, NullLogger<TripImportService>.Instance, CreateReconciler(context));

        await Assert.ThrowsAsync<TripImportValidationException>(() => service.ImportWayfarerKmlAsync(
            ToStream(CreateKml(Guid.NewGuid(), slug)), user.Id, TripImportMode.CreateNew));

        Assert.Equal(4, interceptor.TagLookupCount);
        await using var verification = fixture.CreateContext();
        Assert.Empty(await verification.Trips.Where(trip => trip.UserId == user.Id).ToListAsync());
        Assert.Equal(2, await verification.Tags.CountAsync(tag => tag.Id == bySlug.Id || tag.Id == byName.Id));
        Assert.Empty(await verification.Tags.Where(tag => tag.Slug == slug && tag.Id != bySlug.Id).ToListAsync());
    }

    private static TripImportTagReconciler CreateReconciler(ApplicationDbContext context, ILoggerFactory? loggerFactory = null) =>
        new(context, loggerFactory?.CreateLogger<TripImportTagReconciler>() ?? NullLogger<TripImportTagReconciler>.Instance);

    private static MemoryStream ToStream(string kml) => new(Encoding.UTF8.GetBytes(kml));

    /// <summary>Creates a tag-only versionless-v1 fixture with a native region identity.</summary>
    private static string CreateKml(Guid id, string tags) => $@"<kml xmlns=""http://www.opengis.net/kml/2.2""><Document><name>Trip</name><ExtendedData>
<Data name=""TripId""><value>{id}</value></Data><Data name=""Tags""><value>{tags}</value></Data></ExtendedData>
<Folder><name>Imported Region</name><ExtendedData><Data name=""RegionId""><value>{Guid.NewGuid()}</value></Data></ExtendedData></Folder>
</Document></kml>";

    /// <summary>Identifies the key which PostgreSQL rejects before recovery sees a repaired winner.</summary>
    public enum ConflictKey { Slug, Name }

    /// <summary>Seeds after initial lookups and changes only the externally committed row before recovery.</summary>
    private sealed class ConflictRecoveryInterceptor(
        Func<Task> seedConflict,
        Func<Task> repairWinner,
        int repairAfterLookup = 2) : DbCommandInterceptor
    {
        private bool _seeded;
        private bool _repaired;

        /// <summary>Gets the number of reconciliation queries against the tag table.</summary>
        public int TagLookupCount { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO \"Tags\"", StringComparison.Ordinal) && !_seeded)
            {
                _seeded = true;
                await seedConflict();
            }
            else if (command.CommandText.Contains("FROM \"Tags\"", StringComparison.Ordinal))
            {
                TagLookupCount++;
                if (!_repaired && TagLookupCount == repairAfterLookup + 1)
                {
                    _repaired = true;
                    await repairWinner();
                }
            }

            return result;
        }
    }
}
