using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

public sealed class LocationRecoveryConvergenceTests : TestBase
{
    [Fact]
    public async Task ImportThenDrain_ReusesOneServerLocation()
    {
        await using var scenario = await LocationRecoveryScenario.CreateAsync(CreateDbContext(), "user-a");
        var key = Guid.NewGuid();
        var importedId = await scenario.ImportAsync(key);
        var drainedId = await scenario.DrainAsync(key);
        Assert.Equal(importedId, drainedId);
        Assert.Equal(1, scenario.LocationCount(key));
    }

    [Fact]
    public async Task DrainThenImport_ReportsReuseAndLeavesOneServerLocation()
    {
        await using var scenario = await LocationRecoveryScenario.CreateAsync(CreateDbContext(), "user-a");
        var key = Guid.NewGuid();
        var drainedId = await scenario.DrainAsync(key);
        var result = await scenario.ImportWithResultAsync(key);
        Assert.Equal(drainedId, result.LocationId);
        Assert.Equal(1, result.SkippedDuplicates);
        Assert.Equal(1, scenario.LocationCount(key));
    }

    [Fact]
    public async Task SameGuid_IsIndependentAcrossAuthenticatedUsers()
    {
        var db = CreateDbContext();
        await using var userA = await LocationRecoveryScenario.CreateAsync(db, "user-a");
        await using var userB = await LocationRecoveryScenario.CreateAsync(db, "user-b");
        var key = Guid.NewGuid();
        var a = await userA.DrainAsync(key);
        var b = await userB.DrainAsync(key);
        Assert.NotEqual(a, b);
        Assert.Equal(2, db.Locations.Count(location => location.IdempotencyKey == key));
    }
}
