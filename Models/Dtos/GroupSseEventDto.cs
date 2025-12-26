using System;
using System.Text.Json.Serialization;

namespace Wayfarer.Models.Dtos;

/// <summary>
/// Unified SSE event payload for group streams. All events include a 'type' discriminator
/// to allow clients to handle different event kinds from a single consolidated stream.
/// </summary>
public sealed class GroupSseEventDto
{
    /// <summary>
    /// Event type discriminator. Valid values:
    /// - "location": Location update for a group member
    /// - "location-deleted": Location was deleted
    /// - "visibility-changed": Peer visibility setting changed
    /// - "member-joined": New member joined the group
    /// - "member-left": Member voluntarily left the group
    /// - "member-removed": Member was removed by owner/manager
    /// - "invite-created": New invitation was created
    /// - "invite-declined": Invitation was declined
    /// - "invite-revoked": Invitation was revoked/cancelled
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// User identifier (present on most events).
    /// </summary>
    [JsonPropertyName("userId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserId { get; init; }

    /// <summary>
    /// Username of the user (present on location events).
    /// </summary>
    [JsonPropertyName("userName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserName { get; init; }

    /// <summary>
    /// Location ID (present on location events).
    /// </summary>
    [JsonPropertyName("locationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int LocationId { get; init; }

    /// <summary>
    /// UTC timestamp (present on location events).
    /// </summary>
    [JsonPropertyName("timestampUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTime TimestampUtc { get; init; }

    /// <summary>
    /// Whether the location is live (within threshold window).
    /// Present on location events.
    /// </summary>
    [JsonPropertyName("isLive")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsLive { get; init; }

    /// <summary>
    /// Location type indicator (e.g., "check-in"). Present on location events.
    /// </summary>
    [JsonPropertyName("locationType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LocationType { get; init; }

    /// <summary>
    /// Whether peer visibility is disabled (present on visibility-changed events).
    /// </summary>
    [JsonPropertyName("disabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Disabled { get; init; }

    /// <summary>
    /// Invitation ID (present on invite-related and member-joined events).
    /// </summary>
    [JsonPropertyName("invitationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? InvitationId { get; init; }

    /// <summary>
    /// Invite ID for revoke events (legacy field name compatibility).
    /// </summary>
    [JsonPropertyName("inviteId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? InviteId { get; init; }

    #region Factory Methods

    /// <summary>
    /// Creates a location event payload.
    /// </summary>
    public static GroupSseEventDto Location(
        int locationId,
        DateTime timestampUtc,
        string userId,
        string userName,
        bool isLive,
        string? locationType = null) => new()
    {
        Type = "location",
        LocationId = locationId,
        TimestampUtc = timestampUtc,
        UserId = userId,
        UserName = userName,
        IsLive = isLive,
        LocationType = locationType
    };

    /// <summary>
    /// Creates a location-deleted event payload.
    /// </summary>
    public static GroupSseEventDto LocationDeleted(int locationId, string userId) => new()
    {
        Type = "location-deleted",
        LocationId = locationId,
        UserId = userId
    };

    /// <summary>
    /// Creates a visibility-changed event payload.
    /// </summary>
    public static GroupSseEventDto VisibilityChanged(string userId, bool disabled) => new()
    {
        Type = "visibility-changed",
        UserId = userId,
        Disabled = disabled
    };

    /// <summary>
    /// Creates a member-joined event payload.
    /// </summary>
    public static GroupSseEventDto MemberJoined(string userId, Guid? invitationId = null) => new()
    {
        Type = "member-joined",
        UserId = userId,
        InvitationId = invitationId
    };

    /// <summary>
    /// Creates a member-left event payload.
    /// </summary>
    public static GroupSseEventDto MemberLeft(string userId) => new()
    {
        Type = "member-left",
        UserId = userId
    };

    /// <summary>
    /// Creates a member-removed event payload.
    /// </summary>
    public static GroupSseEventDto MemberRemoved(string userId) => new()
    {
        Type = "member-removed",
        UserId = userId
    };

    /// <summary>
    /// Creates an invite-created event payload.
    /// </summary>
    public static GroupSseEventDto InviteCreated(Guid invitationId) => new()
    {
        Type = "invite-created",
        InvitationId = invitationId
    };

    /// <summary>
    /// Creates an invite-declined event payload.
    /// </summary>
    public static GroupSseEventDto InviteDeclined(string userId, Guid invitationId) => new()
    {
        Type = "invite-declined",
        UserId = userId,
        InvitationId = invitationId
    };

    /// <summary>
    /// Creates an invite-revoked event payload.
    /// </summary>
    public static GroupSseEventDto InviteRevoked(Guid inviteId) => new()
    {
        Type = "invite-revoked",
        InviteId = inviteId
    };

    #endregion
}
