using System;
using System.Text.Json.Serialization;

namespace Wayfarer.Models.Dtos;

/// <summary>
/// Payload emitted via SSE when a mobile client receives a location update.
/// </summary>
public sealed class MobileLocationSseEventDto
{
    /// <summary>
    /// Legacy PascalCase identifier retained for existing web listeners.
    /// </summary>
    public int LocationId { get; init; }

    /// <summary>
    /// Lower camel-case identifier required by mobile clients.
    /// </summary>
    [JsonPropertyName("locationId")]
    public int LocationIdLower => LocationId;

    /// <summary>
    /// Legacy timestamp field kept for web compatibility.
    /// </summary>
    public DateTime TimeStamp { get; init; }

    /// <summary>
    /// UTC timestamp exposed for mobile clients.
    /// </summary>
    [JsonPropertyName("timestampUtc")]
    public DateTime TimestampUtc => TimeStamp;

    /// <summary>
    /// Identifier of the user that produced the location update.
    /// </summary>
    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    /// <summary>
    /// Username of the user that produced the location update.
    /// </summary>
    [JsonPropertyName("userName")]
    public string UserName { get; init; } = string.Empty;

    /// <summary>
    /// True when the location falls within the live threshold window.
    /// </summary>
    [JsonPropertyName("isLive")]
    public bool IsLive { get; init; }

    /// <summary>
    /// Optional type indicator (e.g., "check-in"); omitted when null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }
}
