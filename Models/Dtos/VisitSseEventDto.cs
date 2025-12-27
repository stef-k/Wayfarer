using System;
using System.Text.Json.Serialization;

namespace Wayfarer.Models.Dtos;

/// <summary>
/// SSE event payload for visit notifications.
/// Broadcast when a user's visit to a planned place is confirmed.
/// </summary>
public sealed class VisitSseEventDto
{
    /// <summary>
    /// Event type discriminator. Currently only "visit_started".
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "visit_started";

    /// <summary>
    /// The unique identifier of the visit event.
    /// </summary>
    [JsonPropertyName("visitId")]
    public Guid VisitId { get; init; }

    /// <summary>
    /// The trip ID containing the visited place.
    /// </summary>
    [JsonPropertyName("tripId")]
    public Guid TripId { get; init; }

    /// <summary>
    /// The trip name (snapshot at visit time).
    /// </summary>
    [JsonPropertyName("tripName")]
    public string TripName { get; init; } = string.Empty;

    /// <summary>
    /// The place ID that was visited (null if place was deleted).
    /// </summary>
    [JsonPropertyName("placeId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? PlaceId { get; init; }

    /// <summary>
    /// The place name (snapshot at visit time).
    /// </summary>
    [JsonPropertyName("placeName")]
    public string PlaceName { get; init; } = string.Empty;

    /// <summary>
    /// The region name containing the place.
    /// </summary>
    [JsonPropertyName("regionName")]
    public string RegionName { get; init; } = string.Empty;

    /// <summary>
    /// UTC timestamp when the visit was confirmed.
    /// </summary>
    [JsonPropertyName("arrivedAtUtc")]
    public DateTime ArrivedAtUtc { get; init; }

    /// <summary>
    /// Latitude of the visited place.
    /// </summary>
    [JsonPropertyName("latitude")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Latitude { get; init; }

    /// <summary>
    /// Longitude of the visited place.
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

    /// <summary>
    /// Creates a visit_started event from a PlaceVisitEvent.
    /// </summary>
    public static VisitSseEventDto FromVisitEvent(PlaceVisitEvent visit) => new()
    {
        Type = "visit_started",
        VisitId = visit.Id,
        TripId = visit.TripIdSnapshot,
        TripName = visit.TripNameSnapshot ?? string.Empty,
        PlaceId = visit.PlaceId,
        PlaceName = visit.PlaceNameSnapshot ?? string.Empty,
        RegionName = visit.RegionNameSnapshot ?? string.Empty,
        ArrivedAtUtc = visit.ArrivedAtUtc,
        Latitude = visit.PlaceLocationSnapshot?.Y,
        Longitude = visit.PlaceLocationSnapshot?.X,
        IconName = visit.IconNameSnapshot,
        MarkerColor = visit.MarkerColorSnapshot
    };
}
