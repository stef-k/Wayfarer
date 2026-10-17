using System.Text.Json;
using Wayfarer.Models;
using Wayfarer.Services.ExternalRouting;

namespace Wayfarer.Services;

/// <summary>Portable, credential-free provenance of the single accepted route, independent of duration intent.</summary>
public sealed record SegmentRouteProvenance(
    string? Provider, string? StorageMode, string? MappingMode, DateTimeOffset? GeneratedAt,
    string? Attribution, string? InstructionsJson, Guid? TransportProfileId,
    Guid? ProviderConfigurationId, int? ProviderConfigurationVersion)
{
    /// <summary>Recognizes supported persistent source evidence; no client-supplied Boolean grants authority.</summary>
    public bool IsQualifiedProvider() => Provider == "geoapify" && StorageMode == "persistent";

    /// <summary>Copies existing persisted metadata without inferring a provider from numerical differences.</summary>
    public static SegmentRouteProvenance? Capture(Segment segment) =>
        segment.RouteProvider == null && segment.RouteStorageMode == null && segment.RouteMappingMode == null
        && segment.RouteGeneratedAt == null && segment.RouteAttribution == null && segment.RouteInstructionsJson == null
        && segment.RouteTransportProfileId == null && segment.RouteProviderConfigurationId == null
        && segment.RouteProviderConfigurationVersion == null ? null : new(
            segment.RouteProvider, segment.RouteStorageMode, segment.RouteMappingMode, segment.RouteGeneratedAt,
            segment.RouteAttribution, segment.RouteInstructionsJson, segment.RouteTransportProfileId,
            segment.RouteProviderConfigurationId, segment.RouteProviderConfigurationVersion);

    /// <summary>Installs only the bounded source metadata; it never authorizes credentials or provider execution.</summary>
    public void ApplyTo(Segment segment)
    {
        segment.RouteProvider = Provider;
        segment.RouteStorageMode = StorageMode;
        segment.RouteMappingMode = MappingMode;
        segment.RouteGeneratedAt = GeneratedAt?.ToUniversalTime();
        segment.RouteAttribution = Attribution;
        segment.RouteInstructionsJson = InstructionsJson;
        segment.RouteTransportProfileId = TransportProfileId;
        segment.RouteProviderConfigurationId = ProviderConfigurationId;
        segment.RouteProviderConfigurationVersion = ProviderConfigurationVersion;
    }

    /// <summary>Rejects unsupported retention rights, unbounded metadata and invalid normalized instructions.</summary>
    public void Validate(Segment segment)
    {
        if (Provider?.Length > 24 || StorageMode?.Length > 16 || MappingMode?.Length > 80
            || Attribution?.Length > 500 || InstructionsJson?.Length > 65535
            || ProviderConfigurationVersion < 0 || TransportProfileId == Guid.Empty || ProviderConfigurationId == Guid.Empty)
            throw new InvalidOperationException("Segment route provenance is outside its supported bounds.");
        if (StorageMode != null && StorageMode != "persistent"
            || Provider != null && Provider != "geoapify"
            || MappingMode != null && !ProviderDirectionsCatalog.TryParse(Provider, MappingMode, out _)
            || IsQualifiedProvider() && segment.RouteGeometry == null)
            throw new InvalidOperationException("Segment route provenance does not permit persistent retention.");
        if (InstructionsJson == null) return;
        try
        {
            var instructions = JsonSerializer.Deserialize<RouteInstruction[]>(InstructionsJson)
                ?? throw new JsonException();
            if (instructions.Length > 0 && segment.RouteGeometry == null) throw new JsonException();
            if (instructions.Any(item => item == null || item.Text == null || item.Text.Length > 500 || item.Type == null || item.Type.Length > 80
                || item.FromIndex < 0 || item.ToIndex < item.FromIndex || item.ToIndex >= segment.RouteGeometry?.NumPoints
                || !double.IsFinite(item.DistanceMetres) || item.DistanceMetres < 0
                || !double.IsFinite(item.DurationSeconds) || item.DurationSeconds < 0))
                throw new JsonException();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Segment route instructions are invalid.");
        }
    }

    /// <summary>Clears source claims atomically when the accepted route or its anchors change.</summary>
    public static void Clear(Segment segment)
    {
        segment.RouteProvider = null;
        segment.RouteStorageMode = null;
        segment.RouteMappingMode = null;
        segment.RouteGeneratedAt = null;
        segment.RouteAttribution = null;
        segment.RouteInstructionsJson = null;
        segment.RouteTransportProfileId = null;
        segment.RouteProviderConfigurationId = null;
        segment.RouteProviderConfigurationVersion = null;
    }
}

/// <summary>Validates the one saved measurement set without substituting today's planning calculations.</summary>
public static class SegmentMeasurementState
{
    /// <summary>Retains finite nonnegative estimates, valid nulls and explicit Manual intent, including historical precision.</summary>
    public static void Validate(Segment segment)
    {
        ValidateValues(segment.EstimatedDistanceKm, segment.EstimatedDuration, segment.EstimatedDurationSource);
        if (segment.EstimatedDuration is { Ticks: var ticks } && ticks % 10 != 0)
            throw new InvalidOperationException("Segment duration exceeds PostgreSQL microsecond precision.");
        SegmentRouteProvenance.Capture(segment)?.Validate(segment);
    }

    /// <summary>Checks a proposed active set before it is applied to a tracked aggregate.</summary>
    internal static void ValidateValues(double? distance, TimeSpan? duration, EstimatedDurationSource source)
    {
        if (!Enum.IsDefined(source))
            throw new InvalidOperationException("Segment duration provenance is invalid.");
        if (distance is { } value && (!double.IsFinite(value) || value < 0) || duration is { Ticks: < 0 })
            throw new InvalidOperationException("Segment measurements must be finite and non-negative.");
        if (source == EstimatedDurationSource.Manual && duration == null)
            throw new InvalidOperationException("Manual duration is required.");
    }
}
