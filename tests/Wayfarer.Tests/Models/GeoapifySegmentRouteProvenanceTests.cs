using Wayfarer.Models;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Locks the bounded nullable Segment route-provenance schema.</summary>
public sealed class GeoapifySegmentRouteProvenanceTests
{
    [Fact]
    public void SegmentOwnsOnlyNormalizedNullableRouteAuthority()
    {
        var names = typeof(Segment).GetProperties().Select(property => property.Name).ToHashSet();

        Assert.Contains("RouteInstructionsJson", names);
        Assert.Contains("RouteProvider", names);
        Assert.Contains("RouteProviderConfigurationId", names);
        Assert.Contains("RouteProviderConfigurationVersion", names);
        Assert.Contains("RouteTransportProfileId", names);
        Assert.Contains("RouteMappingMode", names);
        Assert.Contains("RouteGeneratedAt", names);
        Assert.Contains("RouteAttribution", names);
        Assert.Contains("RouteStorageMode", names);
        Assert.DoesNotContain("RouteRawResponse", names);
        Assert.DoesNotContain("RouteCredential", names);
        Assert.DoesNotContain("RouteProviderUrl", names);
    }
}
