using System.Text.Json.Serialization;

namespace Wayfarer.Models.Dtos;

/// <summary>
/// A suggested visit for the "Consider Also" feature.
/// These are potential visits that didn't meet strict matching criteria
/// but have cross-tier evidence suggesting the user may have visited.
/// Requires user confirmation before creating a visit record.
/// </summary>
public sealed class SuggestedVisitDto
{
    /// <summary>
    /// The place ID for this suggestion.
    /// </summary>
    [JsonPropertyName("placeId")]
    public Guid PlaceId { get; init; }

    /// <summary>
    /// The place name.
    /// </summary>
    [JsonPropertyName("placeName")]
    public string PlaceName { get; init; } = string.Empty;

    /// <summary>
    /// The region name containing the place.
    /// </summary>
    [JsonPropertyName("regionName")]
    public string RegionName { get; init; } = string.Empty;

    /// <summary>
    /// The date of the potential visit (local date).
    /// </summary>
    [JsonPropertyName("visitDate")]
    public DateOnly VisitDate { get; init; }

    /// <summary>
    /// Minimum distance from place center achieved in meters.
    /// </summary>
    [JsonPropertyName("minDistanceMeters")]
    public double MinDistanceMeters { get; init; }

    /// <summary>
    /// Number of location pings within Tier 1 radius (e.g., 150m).
    /// </summary>
    [JsonPropertyName("hitsTier1")]
    public int HitsTier1 { get; init; }

    /// <summary>
    /// Number of location pings within Tier 2 radius (e.g., 300m).
    /// </summary>
    [JsonPropertyName("hitsTier2")]
    public int HitsTier2 { get; init; }

    /// <summary>
    /// Number of location pings within Tier 3 radius (e.g., 750m).
    /// </summary>
    [JsonPropertyName("hitsTier3")]
    public int HitsTier3 { get; init; }

    /// <summary>
    /// Total location pings within max suggestion radius.
    /// </summary>
    [JsonPropertyName("hitsTotal")]
    public int HitsTotal { get; init; }

    /// <summary>
    /// Whether a user-invoked check-in was found within range.
    /// This is a strong signal that the user was actually at this place.
    /// </summary>
    [JsonPropertyName("hasUserCheckin")]
    public bool HasUserCheckin { get; init; }

    /// <summary>
    /// Human-readable reason why this place is being suggested.
    /// E.g., "Cross-tier: 8 pings within 300m" or "User checked in nearby"
    /// </summary>
    [JsonPropertyName("suggestionReason")]
    public string SuggestionReason { get; init; } = string.Empty;

    /// <summary>Structured evidence for clients rendering the suggestion in their document locale.</summary>
    [JsonPropertyName("suggestionEvidence")]
    public VisitSuggestionEvidence? SuggestionEvidence { get; init; }

    /// <summary>
    /// UTC timestamp of the first location within range on this date.
    /// </summary>
    [JsonPropertyName("firstSeenUtc")]
    public DateTime FirstSeenUtc { get; init; }

    /// <summary>
    /// UTC timestamp of the last location within range on this date.
    /// </summary>
    [JsonPropertyName("lastSeenUtc")]
    public DateTime LastSeenUtc { get; init; }

    /// <summary>
    /// Place latitude.
    /// </summary>
    [JsonPropertyName("latitude")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Latitude { get; init; }

    /// <summary>
    /// Place longitude.
    /// </summary>
    [JsonPropertyName("longitude")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Longitude { get; init; }

    /// <summary>
    /// Icon name for the place marker.
    /// </summary>
    [JsonPropertyName("iconName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IconName { get; init; }

    /// <summary>
    /// Marker color for the place.
    /// </summary>
    [JsonPropertyName("markerColor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MarkerColor { get; init; }
}

/// <summary>Ping counts and applicable distance thresholds behind the selected suggestion reason.</summary>
public sealed record VisitSuggestionEvidence(int Count, int? RadiusMeters = null, int? AdditionalCount = null, int? AdditionalRadiusMeters = null);
