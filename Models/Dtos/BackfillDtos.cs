using System.Text.Json.Serialization;

namespace Wayfarer.Models.Dtos;

/// <summary>
/// Preview result for the backfill analysis operation.
/// Contains candidates for new visits and stale visits to remove.
/// </summary>
public sealed class BackfillPreviewDto
{
    /// <summary>
    /// The trip ID that was analyzed.
    /// </summary>
    [JsonPropertyName("tripId")]
    public Guid TripId { get; init; }

    /// <summary>
    /// The trip name.
    /// </summary>
    [JsonPropertyName("tripName")]
    public string TripName { get; init; } = string.Empty;

    /// <summary>
    /// Total number of location points scanned.
    /// </summary>
    [JsonPropertyName("locationsScanned")]
    public int LocationsScanned { get; init; }

    /// <summary>
    /// Number of places analyzed (places with coordinates).
    /// </summary>
    [JsonPropertyName("placesAnalyzed")]
    public int PlacesAnalyzed { get; init; }

    /// <summary>
    /// Analysis duration in milliseconds.
    /// </summary>
    [JsonPropertyName("analysisDurationMs")]
    public long AnalysisDurationMs { get; init; }

    /// <summary>
    /// New visit candidates discovered from location history.
    /// </summary>
    [JsonPropertyName("newVisits")]
    public List<BackfillCandidateDto> NewVisits { get; init; } = new();

    /// <summary>
    /// Stale visits that should be removed (place deleted/moved).
    /// </summary>
    [JsonPropertyName("staleVisits")]
    public List<StaleVisitDto> StaleVisits { get; init; } = new();

    /// <summary>
    /// Existing visits for this trip (for display and debugging).
    /// </summary>
    [JsonPropertyName("existingVisits")]
    public List<ExistingVisitDto> ExistingVisits { get; init; } = new();

    /// <summary>
    /// Suggested visits that didn't meet strict criteria but have cross-tier evidence.
    /// These require user confirmation via the "Consider Also" UI.
    /// </summary>
    [JsonPropertyName("suggestedVisits")]
    public List<SuggestedVisitDto> SuggestedVisits { get; init; } = new();
}

/// <summary>
/// A candidate for a new visit discovered during backfill analysis.
/// </summary>
public sealed class BackfillCandidateDto
{
    /// <summary>
    /// The place ID for this visit candidate.
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
    /// The date of the visit (local date).
    /// </summary>
    [JsonPropertyName("visitDate")]
    public DateOnly VisitDate { get; init; }

    /// <summary>
    /// UTC timestamp of the first location within radius on this date.
    /// </summary>
    [JsonPropertyName("firstSeenUtc")]
    public DateTime FirstSeenUtc { get; init; }

    /// <summary>
    /// UTC timestamp of the last location within radius on this date.
    /// </summary>
    [JsonPropertyName("lastSeenUtc")]
    public DateTime LastSeenUtc { get; init; }

    /// <summary>
    /// Number of location pings within radius on this date.
    /// </summary>
    [JsonPropertyName("locationCount")]
    public int LocationCount { get; init; }

    /// <summary>
    /// Average distance from place center in meters.
    /// </summary>
    [JsonPropertyName("avgDistanceMeters")]
    public double AvgDistanceMeters { get; init; }

    /// <summary>
    /// Confidence score (0-100) based on hit count and distance.
    /// </summary>
    [JsonPropertyName("confidence")]
    public int Confidence { get; init; }

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

/// <summary>
/// A stale visit that should be removed during backfill cleanup.
/// </summary>
public sealed class StaleVisitDto
{
    /// <summary>
    /// The visit ID.
    /// </summary>
    [JsonPropertyName("visitId")]
    public Guid VisitId { get; init; }

    /// <summary>
    /// The place ID (null if place was deleted).
    /// </summary>
    [JsonPropertyName("placeId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? PlaceId { get; init; }

    /// <summary>
    /// The place name snapshot from when the visit was created.
    /// </summary>
    [JsonPropertyName("placeName")]
    public string PlaceName { get; init; } = string.Empty;

    /// <summary>
    /// The region name snapshot.
    /// </summary>
    [JsonPropertyName("regionName")]
    public string RegionName { get; init; } = string.Empty;

    /// <summary>
    /// The date of the original visit.
    /// </summary>
    [JsonPropertyName("visitDate")]
    public DateOnly VisitDate { get; init; }

    /// <summary>
    /// Why this visit is considered stale.
    /// </summary>
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// Distance from original location to current place location (if place moved).
    /// </summary>
    [JsonPropertyName("distanceMeters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DistanceMeters { get; init; }
}

/// <summary>
/// An existing visit for display in the backfill preview.
/// </summary>
public sealed class ExistingVisitDto
{
    /// <summary>
    /// The visit ID.
    /// </summary>
    [JsonPropertyName("visitId")]
    public Guid VisitId { get; init; }

    /// <summary>
    /// The place ID (null if place was deleted).
    /// </summary>
    [JsonPropertyName("placeId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? PlaceId { get; init; }

    /// <summary>
    /// The place name snapshot.
    /// </summary>
    [JsonPropertyName("placeName")]
    public string PlaceName { get; init; } = string.Empty;

    /// <summary>
    /// The region name snapshot.
    /// </summary>
    [JsonPropertyName("regionName")]
    public string RegionName { get; init; } = string.Empty;

    /// <summary>
    /// The date of the visit.
    /// </summary>
    [JsonPropertyName("visitDate")]
    public DateOnly VisitDate { get; init; }

    /// <summary>
    /// UTC timestamp when the visit started.
    /// </summary>
    [JsonPropertyName("arrivedAtUtc")]
    public DateTime ArrivedAtUtc { get; init; }

    /// <summary>
    /// Whether the visit is still open.
    /// </summary>
    [JsonPropertyName("isOpen")]
    public bool IsOpen { get; init; }
}

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

/// <summary>
/// Request payload for applying backfill changes.
/// </summary>
public sealed class BackfillApplyRequestDto
{
    /// <summary>
    /// Candidates to create as new visits (from strict matching).
    /// Each item is identified by PlaceId + VisitDate.
    /// </summary>
    [JsonPropertyName("createVisits")]
    public List<BackfillCreateVisitDto> CreateVisits { get; init; } = new();

    /// <summary>
    /// Confirmed suggestions to create as visits (from "Consider Also").
    /// These are user-confirmed suggestions that should be created with source "backfill-user-confirmed".
    /// </summary>
    [JsonPropertyName("confirmedSuggestions")]
    public List<BackfillCreateVisitDto> ConfirmedSuggestions { get; init; } = new();

    /// <summary>
    /// Visit IDs to delete (stale visits).
    /// </summary>
    [JsonPropertyName("deleteVisitIds")]
    public List<Guid> DeleteVisitIds { get; init; } = new();
}

/// <summary>
/// Identifies a visit to create during backfill apply.
/// </summary>
public sealed class BackfillCreateVisitDto
{
    /// <summary>
    /// The place ID.
    /// </summary>
    [JsonPropertyName("placeId")]
    public Guid PlaceId { get; init; }

    /// <summary>
    /// The visit date.
    /// </summary>
    [JsonPropertyName("visitDate")]
    public DateOnly VisitDate { get; init; }

    /// <summary>
    /// UTC timestamp of the first location (for ArrivedAtUtc).
    /// </summary>
    [JsonPropertyName("firstSeenUtc")]
    public DateTime FirstSeenUtc { get; init; }

    /// <summary>
    /// UTC timestamp of the last location (for LastSeenAtUtc/EndedAtUtc).
    /// </summary>
    [JsonPropertyName("lastSeenUtc")]
    public DateTime LastSeenUtc { get; init; }
}

/// <summary>
/// Result of applying backfill changes.
/// </summary>
public sealed class BackfillResultDto
{
    /// <summary>
    /// Whether the operation was successful.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    /// <summary>
    /// Number of visits created from strict matching.
    /// </summary>
    [JsonPropertyName("visitsCreated")]
    public int VisitsCreated { get; init; }

    /// <summary>
    /// Number of visits created from user-confirmed suggestions.
    /// </summary>
    [JsonPropertyName("suggestionsConfirmed")]
    public int SuggestionsConfirmed { get; init; }

    /// <summary>
    /// Number of visits deleted.
    /// </summary>
    [JsonPropertyName("visitsDeleted")]
    public int VisitsDeleted { get; init; }

    /// <summary>
    /// Number of candidates skipped (duplicate or place no longer exists).
    /// </summary>
    [JsonPropertyName("skipped")]
    public int Skipped { get; init; }

    /// <summary>
    /// Optional message with details.
    /// </summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
}

/// <summary>
/// Lightweight metadata for backfill analysis progress feedback.
/// </summary>
public sealed class BackfillInfoDto
{
    /// <summary>
    /// The trip ID.
    /// </summary>
    [JsonPropertyName("tripId")]
    public Guid TripId { get; init; }

    /// <summary>
    /// The trip name.
    /// </summary>
    [JsonPropertyName("tripName")]
    public string TripName { get; init; } = string.Empty;

    /// <summary>
    /// Total number of places in the trip.
    /// </summary>
    [JsonPropertyName("totalPlaces")]
    public int TotalPlaces { get; init; }

    /// <summary>
    /// Number of places with coordinates (analyzable).
    /// </summary>
    [JsonPropertyName("placesWithCoordinates")]
    public int PlacesWithCoordinates { get; init; }

    /// <summary>
    /// Estimated location records to scan (based on date range if provided).
    /// </summary>
    [JsonPropertyName("estimatedLocations")]
    public int EstimatedLocations { get; init; }

    /// <summary>
    /// Estimated analysis duration in seconds.
    /// </summary>
    [JsonPropertyName("estimatedSeconds")]
    public int EstimatedSeconds { get; init; }

    /// <summary>
    /// Number of existing visits for this trip.
    /// </summary>
    [JsonPropertyName("existingVisits")]
    public int ExistingVisits { get; init; }
}
