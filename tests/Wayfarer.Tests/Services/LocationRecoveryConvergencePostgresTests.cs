using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationRecoveryConvergencePostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresFact]
    public async Task ConcurrentSameUserGuid_HasOneWinnerAndDeterministicReuse()
    {
        var user = await fixture.CreateUserAsync();
        var key = Guid.NewGuid();
        try
        {
            await using var first = fixture.CreateContext();
            await using var second = fixture.CreateContext();
            first.Locations.Add(Create(user.Id, key));
            second.Locations.Add(Create(user.Id, key));
            var results = await Task.WhenAll(SaveAsync(first), SaveAsync(second));
            Assert.Equal(1, results.Count(saved => saved));

            await using var verification = fixture.CreateContext();
            var winner = Assert.Single(await verification.Locations.Where(x => x.UserId == user.Id && x.IdempotencyKey == key).ToListAsync());
            Assert.True(winner.Id > 0);
        }
        finally
        {
            await using var cleanup = fixture.CreateContext();
            await cleanup.Locations.Where(x => x.UserId == user.Id && x.IdempotencyKey == key).ExecuteDeleteAsync();
            Assert.Equal(0, await cleanup.Locations.CountAsync(x => x.UserId == user.Id && x.IdempotencyKey == key));
        }
    }

    private static async Task<bool> SaveAsync(ApplicationDbContext db)
    {
        try { await db.SaveChangesAsync(); return true; }
        catch (DbUpdateException) { return false; }
    }

    private static Wayfarer.Models.Location Create(string userId, Guid key) => new()
    {
        UserId = userId, IdempotencyKey = key, Timestamp = DateTime.UtcNow,
        LocalTimestamp = DateTime.UtcNow, TimeZoneId = "UTC",
        Coordinates = new Point(22.2, 37.1) { SRID = 4326 }, Source = "queue-recovery-test"
    };
}
