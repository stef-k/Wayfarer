using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using NetTopologySuite.Geometries;

namespace Wayfarer.Models;

/// <summary>
/// Represents a confirmed visit to a planned trip place.
/// Supports multiple visits (revisits) to the same place over time.
/// Snapshots preserve historical context even if the trip is deleted.
/// </summary>
public class PlaceVisitEvent
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>User identifier from ASP.NET Identity.</summary>
    [BindNever, ValidateNever]
    public string UserId { get; set; } = string.Empty;

    /// <summary>Navigation property to the user.</summary>
    [ValidateNever]
    [JsonIgnore]
    public ApplicationUser? User { get; set; }

    /// <summary>
    /// Optional foreign key to the place. Nullable to support ON DELETE SET NULL,
    /// allowing visit history to survive trip/place deletion.
    /// </summary>
    public Guid? PlaceId { get; set; }

    /// <summary>Navigation property to the place (null if place was deleted).</summary>
    [ValidateNever]
    [JsonIgnore]
    public Place? Place { get; set; }

    // === Visit Lifecycle (UTC) ===

    /// <summary>UTC timestamp when the visit was first detected (from candidate FirstHitUtc).</summary>
    public DateTime ArrivedAtUtc { get; set; }

    /// <summary>UTC timestamp of the last location ping within the place radius.</summary>
    public DateTime LastSeenAtUtc { get; set; }

    /// <summary>
    /// UTC timestamp when the visit ended. Null while the visit is still "open".
    /// Set to LastSeenAtUtc when no pings received within the configured timeout.
    /// </summary>
    public DateTime? EndedAtUtc { get; set; }

    // === Snapshot Fields (Durability) ===

    /// <summary>Snapshot of Trip.Id at time of visit (not a FK, preserved after deletion).</summary>
    public Guid TripIdSnapshot { get; set; }

    /// <summary>Snapshot of Trip.Name at time of visit.</summary>
    public string TripNameSnapshot { get; set; } = string.Empty;

    /// <summary>Snapshot of Region.Name at time of visit.</summary>
    public string RegionNameSnapshot { get; set; } = string.Empty;

    /// <summary>Snapshot of Place.Name at time of visit.</summary>
    public string PlaceNameSnapshot { get; set; } = string.Empty;

    /// <summary>
    /// Snapshot of Place.Location at time of visit.
    /// Preserved for map display even after place deletion.
    /// </summary>
    public Point? PlaceLocationSnapshot { get; set; }

    /// <summary>
    /// Rich-text HTML notes for this visit. Seeded from Place.Notes at creation,
    /// can be edited independently for per-visit annotations.
    /// </summary>
    [ValidateNever]
    public string? NotesHtml { get; set; }

    // === Optional UI Snapshots ===

    /// <summary>Snapshot of Place.IconName at time of visit.</summary>
    [ValidateNever]
    public string? IconNameSnapshot { get; set; }

    /// <summary>Snapshot of Place.MarkerColor at time of visit.</summary>
    [ValidateNever]
    public string? MarkerColorSnapshot { get; set; }

    // === Computed Properties ===

    /// <summary>
    /// Observed dwell time in minutes, calculated from arrival to last seen.
    /// Returns null if timestamps are invalid.
    /// </summary>
    [NotMapped]
    public double? ObservedDwellMinutes =>
        LastSeenAtUtc > ArrivedAtUtc
            ? (LastSeenAtUtc - ArrivedAtUtc).TotalMinutes
            : null;

    /// <summary>
    /// Whether this visit is still open (no EndedAtUtc set).
    /// </summary>
    [NotMapped]
    public bool IsOpen => EndedAtUtc == null;
}
