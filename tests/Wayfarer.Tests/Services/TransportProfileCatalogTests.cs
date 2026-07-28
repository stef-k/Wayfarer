using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Verifies the database-backed transport-profile authority.
/// </summary>
public sealed class TransportProfileCatalogTests : TestBase
{
    /// <summary>
    /// Proves active editor choices are database-backed and deterministically ordered.
    /// </summary>
    [Fact]
    public async Task GetEditorOptionsAsync_ReturnsOnlyActiveProfiles_InCatalogOrder()
    {
        await using var db = CreateDbContext();
        db.Set<TransportProfile>().RemoveRange(db.Set<TransportProfile>());
        db.Set<TransportProfile>().AddRange(
            Profile("z-last", "Zulu", 2, true),
            Profile("a-hidden", "Alpha", 0, false),
            Profile("b-first", "Beta", 1, true));
        await db.SaveChangesAsync();
        var catalog = new TransportProfileCatalog(db);

        var options = await catalog.GetEditorOptionsAsync();

        Assert.Equal(["b-first", "z-last"], options.Select(option => option.Value));
    }

    /// <summary>
    /// Proves existing segments may preserve inactive keys while new selections may not.
    /// </summary>
    [Fact]
    public async Task ResolveEditorModeAsync_PreservesOnlyCurrentInactiveSelection()
    {
        await using var db = CreateDbContext();
        db.Set<TransportProfile>().RemoveRange(db.Set<TransportProfile>());
        db.Set<TransportProfile>().Add(Profile("legacy", "Legacy", 1, false));
        await db.SaveChangesAsync();
        var catalog = new TransportProfileCatalog(db);

        Assert.Equal("legacy", await catalog.ResolveEditorModeAsync("LEGACY", "legacy"));
        Assert.Null(await catalog.ResolveEditorModeAsync("legacy", null));
    }

    /// <summary>
    /// Proves profile dependency counts and the pre-#405 speed-change gate are deterministic.
    /// </summary>
    [Fact]
    public async Task CanChangePlanningSpeedAsync_RejectsReferencedProfile()
    {
        await using var db = CreateDbContext();
        db.Set<TransportProfile>().RemoveRange(db.Set<TransportProfile>());
        var profile = Profile("walk", "Walk", 1, true);
        db.Set<TransportProfile>().Add(profile);
        db.Segments.Add(new Segment { Id = Guid.NewGuid(), UserId = "u", TripId = Guid.NewGuid(), Mode = " WALK " });
        await db.SaveChangesAsync();
        var catalog = new TransportProfileCatalog(db);

        var result = await catalog.CanChangePlanningSpeedAsync(profile.Id, 6);

        Assert.False(result.Allowed);
        Assert.Equal(1, result.ReferencedSegments);
    }

    /// <summary>Proves planning-speed resolution comes from mutable database state and preserves null.</summary>
    [Fact]
    public async Task GetPlanningSpeedAsync_ReturnsDatabaseValueOrManualOnlyNull()
    {
        await using var db = CreateDbContext();
        var walk = await db.Set<TransportProfile>().SingleAsync(profile => profile.Key == "walk");
        walk.PlanningSpeedKmh = 6.5;
        var manual = Profile("custom", "Custom", 999, true);
        manual.PlanningSpeedKmh = null;
        db.Set<TransportProfile>().Add(manual);
        await db.SaveChangesAsync();
        var catalog = new TransportProfileCatalog(db);

        Assert.Equal(6.5, await catalog.GetPlanningSpeedAsync("WALK"));
        Assert.Null(await catalog.GetPlanningSpeedAsync("custom"));
    }

    private static TransportProfile Profile(string key, string label, int sortOrder, bool active) => new()
    {
        Id = Guid.NewGuid(),
        Key = key,
        Label = label,
        Category = "Test",
        PlanningSpeedKmh = 10,
        SortOrder = sortOrder,
        IsActive = active
    };
}
