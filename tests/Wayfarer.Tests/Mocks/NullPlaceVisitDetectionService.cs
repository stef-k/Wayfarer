using NetTopologySuite.Geometries;
using Wayfarer.Services;

namespace Wayfarer.Tests.Mocks;

/// <summary>
/// A no-op implementation of IPlaceVisitDetectionService for testing.
/// </summary>
public class NullPlaceVisitDetectionService : IPlaceVisitDetectionService
{
    public Task ProcessPingAsync(
        string userId,
        Point location,
        double? accuracyMeters,
        CancellationToken cancellationToken = default)
    {
        // No-op for tests
        return Task.CompletedTask;
    }
}
