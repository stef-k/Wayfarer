using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.Services;

public sealed partial class PlaceRegionLifecycleService
{
    /// <summary>Applies resolved scalar and editor metadata intent to the locked row in the existing transaction.</summary>
    private static void ApplyScalarUpdate(Place place, PlaceLifecycleUpdate update)
    {
        var directive = update.EditorMetadata;
        if (directive != null && (directive.Policy == EditorPlaceMetadataPolicy.Replace
            || !string.Equals(place.Address?.Trim() ?? string.Empty, update.Address?.Trim() ?? string.Empty, StringComparison.Ordinal)
            || !CoordinatesEqual(place.Location, update.Location)))
        {
            var metadata = directive.Policy == EditorPlaceMetadataPolicy.Replace ? directive.Replacement : default;
            place.ResolvedFeatureName = metadata.Name;
            place.ResolvedFeatureType = metadata.Type;
            place.AddressEnrichmentProvider = metadata.Provider;
            place.AddressEnrichmentStorageMode = metadata.StorageMode;
            place.AddressEnrichedAt = metadata.EnrichedAt;
        }
        place.Name = update.Name!;
        place.Notes = update.Notes;
        place.Address = update.Address;
        place.IconName = update.IconName;
        place.MarkerColor = update.MarkerColor;
        place.Location = CopyPoint(update.Location);
    }
}

/// <summary>
/// Complete scalar draft by default. Only legacy partial PUT sets PreserveOmittedFields: null means Unchanged,
/// and Address/enrichment remain canonical because that DTO cannot edit them. Omitted editor metadata retains legacy behavior.
/// </summary>
public sealed record PlaceLifecycleUpdate(Guid? RegionId, string? Name, string? Notes, string? Address, string? IconName,
    string? MarkerColor, Point? Location, int? DisplayOrder = null, EditorPlaceMetadataDirective? EditorMetadata = null,
    bool PreserveOmittedFields = false)
{
    /// <summary>Resolves legacy omissions separately on every locked reload, without changing complete Web draft semantics.</summary>
    internal PlaceLifecycleUpdate ResolveAgainst(Place place) => !PreserveOmittedFields ? this : this with
    {
        RegionId = RegionId ?? place.RegionId,
        Name = Name ?? place.Name,
        Notes = Notes ?? place.Notes,
        Address = place.Address,
        IconName = IconName ?? place.IconName,
        MarkerColor = MarkerColor ?? place.MarkerColor,
        Location = Location ?? place.Location,
        EditorMetadata = null
    };
}

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
