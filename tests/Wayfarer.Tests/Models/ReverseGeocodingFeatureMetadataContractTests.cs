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

    /// <summary>Proves explicit timezone designators preserve their instant and normalize to UTC.</summary>
    [Theory]
    [InlineData("2026-08-28T12:00:00Z", "2026-08-28T12:00:00+00:00")]
    [InlineData("2026-08-28T15:00:00+03:00", "2026-08-28T12:00:00+00:00")]
    public void ImportedTupleAcceptsOnlyExplicitOffsetsAndNormalizesToUtc(
        string timestamp, string expectedTimestamp)
    {
        var normalized = ResolvedFeatureMetadata.NormalizeImported(
            "Tower", "building", "geoapify", "persistent", timestamp);

        Assert.Equal(DateTimeOffset.Parse(expectedTimestamp), normalized.EnrichedAt);
        Assert.Equal(TimeSpan.Zero, normalized.EnrichedAt?.Offset);
    }

    /// <summary>Proves missing or malformed offsets clear the complete imported tuple.</summary>
    [Theory]
    [InlineData("2026-08-28T12:00:00")]
    [InlineData("2026-08-28T12:00:00+03:7x")]
    public void ImportedTupleRejectsTimestampsWithoutValidExplicitOffsets(string timestamp)
    {
        var normalized = ResolvedFeatureMetadata.NormalizeImported(
            "Tower", "building", "geoapify", "persistent", timestamp);

        Assert.Equal(default, normalized);
    }

    /// <summary>Proves valid provenance is independent from optional feature metadata.</summary>
    [Fact]
    public void ImportedTuplePreservesValidMetadataFreeMapboxProvenance()
    {
        var normalized = ResolvedFeatureMetadata.NormalizeImported(
            null, null, "mapbox", "permanent", "2026-08-28T12:00:00Z");

        Assert.Equal((null, null, "mapbox", "permanent", new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero)),
            (normalized.Name, normalized.Type, normalized.Provider, normalized.StorageMode, normalized.EnrichedAt));
    }

    /// <summary>Proves incoherent provenance clears both provenance and free-floating metadata.</summary>
    [Theory]
    [InlineData("Tower", "building", null, null, null)]
    [InlineData("Tower", "building", "unknown", "permanent", "2026-08-28T12:00:00Z")]
    [InlineData("Tower", "building", "mapbox", "persistent", "2026-08-28T12:00:00Z")]
    [InlineData("Tower", "building", "mapbox", "permanent", "not-a-time")]
    public void ImportedTupleClearsInvalidProvenance(
        string? name, string? type, string? provider, string? storageMode, string? timestamp)
    {
        Assert.Equal(default, ResolvedFeatureMetadata.NormalizeImported(name, type, provider, storageMode, timestamp));
    }

    /// <summary>Proves invalid optional fields do not erase otherwise valid provenance.</summary>
    [Fact]
    public void ImportedTupleClearsOnlyInvalidOptionalMetadata()
    {
        var normalized = ResolvedFeatureMetadata.NormalizeImported(
            new string('n', 501), "unknown-type", "geoapify", "persistent", "2026-08-28T12:00:00Z");

        Assert.Equal((null, null, "geoapify", "persistent"),
            (normalized.Name, normalized.Type, normalized.Provider, normalized.StorageMode));
    }
}
