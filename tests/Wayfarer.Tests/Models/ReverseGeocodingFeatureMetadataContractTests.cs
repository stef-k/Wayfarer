using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services.LocationProviders;
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

    [Fact]
    public void ImportedTupleRequiresValidCompleteProvenance()
    {
        var valid = ResolvedFeatureMetadata.NormalizeImported(
            " Tokyo Tower ", "BUILDING", "geoapify", "persistent", "2026-08-28T12:00:00Z");
        var invalid = ResolvedFeatureMetadata.NormalizeImported(
            "Tokyo Tower", "building", "geoapify", null, "2026-08-28T12:00:00Z");

        Assert.Equal(("Tokyo Tower", "building", "geoapify", "persistent"),
            (valid.Name, valid.Type, valid.Provider, valid.StorageMode));
        Assert.Equal(default, invalid);
    }
}
