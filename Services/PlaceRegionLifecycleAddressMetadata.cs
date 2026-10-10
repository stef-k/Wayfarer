using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.Services;

public sealed partial class PlaceRegionLifecycleService
{
    /// <summary>Applies editor metadata intent against the fresh locked row in the existing transaction.</summary>
    private static void ApplyAddressUpdate(Place place, PlaceLifecycleUpdate update)
    {
        var directive = update.EditorMetadata;
        if (directive != null && (directive.Policy == EditorPlaceMetadataPolicy.Replace
            || !string.Equals(place.Address?.Trim() ?? string.Empty, update.Address.Trim(), StringComparison.Ordinal)
            || !CoordinatesEqual(place.Location, update.Location)))
        {
            var metadata = directive.Policy == EditorPlaceMetadataPolicy.Replace ? directive.Replacement : default;
            place.ResolvedFeatureName = metadata.Name;
            place.ResolvedFeatureType = metadata.Type;
            place.AddressEnrichmentProvider = metadata.Provider;
            place.AddressEnrichmentStorageMode = metadata.StorageMode;
            place.AddressEnrichedAt = metadata.EnrichedAt;
        }
        place.Address = update.Address;
    }
}

/// <summary>Allowlisted scalar state; omitted editor metadata retains legacy caller behavior.</summary>
public sealed record PlaceLifecycleUpdate(Guid RegionId, string Name, string Notes, string Address, string IconName,
    string MarkerColor, Point? Location, int? DisplayOrder = null, EditorPlaceMetadataDirective? EditorMetadata = null);

/// <summary>Distinguishes compatibility policy from a complete successful provider replacement.</summary>
public enum EditorPlaceMetadataPolicy
{
    /// <summary>Retains canonical metadata only when normalized address and exact nullable coordinates match.</summary>
    RetainCompatible,
    /// <summary>Writes all five replacement fields, including optional null features and the frozen timestamp.</summary>
    Replace
}

/// <summary>Immutable editor intent; only Replace uses the complete successful enrichment tuple.</summary>
public sealed record EditorPlaceMetadataDirective(EditorPlaceMetadataPolicy Policy, ResolvedFeatureTuple Replacement = default);
