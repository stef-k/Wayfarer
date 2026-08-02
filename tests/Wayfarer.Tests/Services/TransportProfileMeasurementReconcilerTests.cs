using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Verifies atomic profile-speed reconciliation semantics independently of the admin controller.</summary>
public sealed class TransportProfileMeasurementReconcilerTests
{
    /// <summary>Positive speed changes recalculate Automatic segments and preserve Manual durations.</summary>
    [Fact]
    public async Task ReconcileAsync_RecalculatesAutomaticAndPreservesManual()
    {
        await using var context = CreateContext();
        var seeded = await SeedAsync(context);

        var result = await TransportProfileMeasurementReconciler.ReconcileAsync(
            context, seeded.ProfileId, 10, "admin", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.TotalReferences);
        Assert.Equal(1, result.AutomaticReferences);
        Assert.Equal(1, result.ManualReferences);
        var segments = await context.Segments.OrderBy(item => item.Id).ToListAsync();
        var automatic = segments.Single(item => item.EstimatedDurationSource == EstimatedDurationSource.Automatic);
        var manual = segments.Single(item => item.EstimatedDurationSource == EstimatedDurationSource.Manual);
        Assert.NotNull(automatic.EstimatedDuration);
        Assert.Equal(TimeSpan.FromMinutes(7), manual.EstimatedDuration);
        Assert.Equal(10, (await context.Set<TransportProfile>().SingleAsync()).PlanningSpeedKmh);
        Assert.Single(context.AuditLogs);
    }

    /// <summary>Confirmed clearing makes Automatic duration unavailable without changing Manual duration.</summary>
    [Fact]
    public async Task ReconcileAsync_ClearingSpeedClearsOnlyAutomaticDuration()
    {
        await using var context = CreateContext();
        var seeded = await SeedAsync(context);

        var result = await TransportProfileMeasurementReconciler.ReconcileAsync(
            context, seeded.ProfileId, null, "admin", CancellationToken.None);

        Assert.True(result.Succeeded);
        var segments = await context.Segments.ToListAsync();
        Assert.Null(segments.Single(item => item.EstimatedDurationSource == EstimatedDurationSource.Automatic).EstimatedDuration);
        Assert.Equal(TimeSpan.FromMinutes(7), segments.Single(item => item.EstimatedDurationSource == EstimatedDurationSource.Manual).EstimatedDuration);
    }

    private static ApplicationDbContext CreateContext()
    {
        var services = new ServiceCollection().AddEntityFrameworkInMemoryDatabase().BuildServiceProvider();
        return new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, services);
    }

    private static async Task<(Guid ProfileId, Guid TripId)> SeedAsync(ApplicationDbContext context)
    {
        var user = new ApplicationUser { Id = Guid.NewGuid().ToString(), UserName = Guid.NewGuid().ToString(), DisplayName = "owner" };
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, User = user, Name = "trip" };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = user.Id, Name = "region" };
        var from = new Place { Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = user.Id, Name = "from", Location = new Point(0, 0) { SRID = 4326 } };
        var to = new Place { Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = user.Id, Name = "to", Location = new Point(0.1, 0) { SRID = 4326 } };
        var profile = new TransportProfile { Id = Guid.NewGuid(), Key = "walk", Label = "Walk", Category = "Land", PlanningSpeedKmh = 5, IsActive = true };
        context.AddRange(user, trip, region, from, to, profile,
            Segment(trip, profile, from, to, EstimatedDurationSource.Automatic, TimeSpan.FromHours(1)),
            Segment(trip, profile, from, to, EstimatedDurationSource.Manual, TimeSpan.FromMinutes(7)));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return (profile.Id, trip.Id);
    }

    private static Segment Segment(Trip trip, TransportProfile profile, Place from, Place to, EstimatedDurationSource source, TimeSpan duration) =>
        new()
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = trip.UserId,
            FromPlace = from, FromPlaceId = from.Id, ToPlace = to, ToPlaceId = to.Id,
            Mode = profile.Key, TransportProfile = profile, TransportProfileId = profile.Id,
            EstimatedDurationSource = source, EstimatedDuration = duration
        };
}
