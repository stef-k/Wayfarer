using Wayfarer.Models;
using Wayfarer.Models.Dtos.TripViewer;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>
/// Verifies the read-only Trip Viewer state mapper and redaction contract.
/// </summary>
public sealed class TripViewerStateMapperTests : TestBase
{
    [Fact]
    public void ToPrivateState_ReturnsServerDerivedPrivateMode()
    {
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var trip = TestDataFixtures.CreateTrip(owner, isPublic: true);

        var state = TripViewerStateMapper.ToPrivateState(trip, Array.Empty<PlaceVisitEvent>(), new QueryCollection());

        Assert.Equal("private", state.ViewerMode);
    }
}
