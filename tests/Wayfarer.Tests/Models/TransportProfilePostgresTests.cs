using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Executes the transport-profile migration invariants on the opt-in isolated PostgreSQL database.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class TransportProfilePostgresTests
{
    private readonly PostgresImportTestFixture _fixture;

    /// <summary>Initializes provider tests over the guarded shared fixture.</summary>
    public TransportProfilePostgresTests(PostgresImportTestFixture fixture) => _fixture = fixture;

    /// <summary>Proves Mode-only writers attach an inactive compatibility profile without rewriting public mode text.</summary>
    [PostgresFact]
    public async Task SegmentInsert_AttachesUnknownCompatibilityProfile_AndPreservesMode()
    {
        _fixture.RequireAvailable();
        var user = await _fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, User = user, Name = "Transport profile fixture" };
        var mode = $"Legacy Mode {Guid.NewGuid():N}";
        var segment = new Segment { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id, Mode = mode };
        _fixture.RegisterTrip(trip.Id);

        await using (var context = _fixture.CreateContext())
        {
            context.Trips.Add(trip);
            context.Segments.Add(segment);
            await context.SaveChangesAsync();
        }

        await using var verification = _fixture.CreateContext();
        var stored = await verification.Segments.AsNoTracking().SingleAsync(item => item.Id == segment.Id);
        var profile = await verification.Set<TransportProfile>().AsNoTracking().SingleAsync(item => item.Id == stored.TransportProfileId);
        _fixture.RegisterTransportProfile(profile.Id);
        Assert.Equal(mode, stored.Mode);
        Assert.False(profile.IsActive);
        Assert.False(profile.IsSeeded);
        Assert.Null(profile.PlanningSpeedKmh);
        Assert.NotEqual(0u, profile.RowVersion);
    }

    /// <summary>Proves PostgreSQL rejects non-normalized keys and non-finite planning speeds.</summary>
    [PostgresTheory]
    [InlineData(" Invalid ", 10d)]
    [InlineData("valid-key", double.PositiveInfinity)]
    public async Task Constraints_RejectInvalidKeyAndInfiniteSpeed(string key, double speed)
    {
        _fixture.RequireAvailable();
        await using var context = _fixture.CreateContext();
        context.Set<TransportProfile>().Add(new TransportProfile
        {
            Id = Guid.NewGuid(), Key = key, Label = "Invalid", Category = "Test", PlanningSpeedKmh = speed
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }
}
