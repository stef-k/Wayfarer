using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Dtos.Editor;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Locks the shared persistence and presentation shape for named reverse-geocoding features.</summary>
public sealed class ReverseGeocodingFeatureMetadataContractTests
{
    [Fact]
    public void PersistenceAndSharedDtosExposeFeatureMetadata()
    {
        Assert.Equal("ResolvedFeatureName", nameof(Location.ResolvedFeatureName));
        Assert.Equal("ResolvedFeatureType", nameof(Location.ResolvedFeatureType));
        Assert.Equal("ResolvedFeatureName", nameof(Place.ResolvedFeatureName));
        Assert.Equal("ResolvedFeatureType", nameof(Place.ResolvedFeatureType));
        Assert.NotNull(typeof(PlaceDto).GetProperty("ResolvedFeatureName"));
        Assert.NotNull(typeof(EditorPlaceDto).GetProperty("ResolvedFeatureType"));
    }
}
