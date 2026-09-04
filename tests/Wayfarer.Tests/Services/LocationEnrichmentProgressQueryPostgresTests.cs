using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Point = NetTopologySuite.Geometries.Point;

namespace Wayfarer.Tests.Services;

/// <summary>Proves enrichment progress remains scalar and cardinality-independent on PostgreSQL.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationEnrichmentProgressQueryPostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresFact]
    public async Task IncompleteCountIncludesOnlyDurableGeoapifyRowsMissingPlace()
    {
        var user = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var now = DateTimeOffset.UtcNow;
        db.Locations.AddRange(
            Partial(user.Id, "geoapify", "persistent", now, addressNumberOnly: true),
            Partial(user.Id, null, null, null),
            Partial(user.Id, "mapbox", "permanent", now),
            Partial(user.Id, "geoapify", "persistent", now, place: "Alexandroupolis"));
        await db.SaveChangesAsync();

        var result = await new LocationEnrichmentProgressQuery(db)
            .ProjectAsync(user.Id, Binding(), DateTime.UtcNow);

        Assert.Equal(1, result.IncompleteProviderAddresses);
    }

    [PostgresFact]
    public async Task LargeProjectionExecutesOnlyFixedAggregateSql()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        await SeedAsync(user, 32);
        var firstRecorder = new CommandRecorder();
        await using (var first = fixture.CreateContext(firstRecorder))
        {
            var result = await new LocationEnrichmentProgressQuery(first)
                .ProjectAsync(user.Id, Binding(), DateTime.UtcNow);
            Assert.Equal(32, result.RunnableRemaining);
        }

        await SeedAsync(user, 224);
        var largeRecorder = new CommandRecorder();
        await using (var large = fixture.CreateContext(largeRecorder))
        {
            var result = await new LocationEnrichmentProgressQuery(large)
                .ProjectAsync(user.Id, Binding(), DateTime.UtcNow);
            Assert.Equal(256, result.RunnableRemaining);
        }

        Assert.Equal(firstRecorder.Aggregates.Count, largeRecorder.Aggregates.Count);
        Assert.Equal(6, largeRecorder.Aggregates.Count);
        Assert.Contains(largeRecorder.Aggregates, sql => sql.Contains("count", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(largeRecorder.Aggregates, sql => sql.Contains("min", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(largeRecorder.Aggregates, sql => sql.Contains(" IN (@", StringComparison.OrdinalIgnoreCase));
        Assert.All(largeRecorder.Aggregates, sql => Assert.Contains("Locations", sql));
    }

    private async Task SeedAsync(Wayfarer.Models.ApplicationUser user, int count)
    {
        await using var context = fixture.CreateContext();
        for (var index = 0; index < count; index++)
            context.Locations.Add(TestDataFixtures.CreateLocation(user));
        await context.SaveChangesAsync();
    }

    private static PersonalProviderAuthorityBinding Binding() => new("geoapify", Guid.NewGuid(),
        1, 1, 1, PersonalProviderVerification.Verified, 1, 1, null, null, null);

    private static Location Partial(string userId, string? provider, string? storage,
        DateTimeOffset? resolvedAt, string? place = null, bool addressNumberOnly = false) => new()
    {
        UserId = userId, Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow,
        TimeZoneId = "UTC", Coordinates = new Point(25, 40) { SRID = 4326 },
        Address = addressNumberOnly ? null : "Known address", AddressNumber = addressNumberOnly ? "12" : null,
        Country = addressNumberOnly ? null : "Greece", Place = place, ReverseGeocodingProvider = provider,
        ReverseGeocodingStorageMode = storage, ReverseGeocodedAt = resolvedAt
    };

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Aggregates { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            if (command.CommandText.Contains("Locations", StringComparison.Ordinal))
                Aggregates.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("Locations", StringComparison.Ordinal))
                Aggregates.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
