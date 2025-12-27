using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.Rendering;
using NetTopologySuite.Geometries;

namespace Wayfarer.Models.ViewModels;

/// <summary>
/// ViewModel for editing a PlaceVisitEvent.
/// Contains snapshot fields that can be edited by the user.
/// </summary>
public class VisitEditViewModel
{
    /// <summary>Primary key of the visit event.</summary>
    public Guid Id { get; set; }

    // === Readonly Context Fields ===

    /// <summary>Snapshot of the place name (readonly display).</summary>
    public string PlaceNameSnapshot { get; set; } = string.Empty;

    /// <summary>Snapshot of the trip name (readonly display).</summary>
    public string TripNameSnapshot { get; set; } = string.Empty;

    /// <summary>Snapshot of the region name (readonly display).</summary>
    public string RegionNameSnapshot { get; set; } = string.Empty;

    /// <summary>Trip ID if the trip still exists.</summary>
    public Guid? TripId { get; set; }

    /// <summary>Place ID if the place still exists.</summary>
    public Guid? PlaceId { get; set; }

    // === Editable Timestamp Fields ===

    /// <summary>When the visit started (UTC).</summary>
    [Required]
    [Display(Name = "Arrived At")]
    public DateTime ArrivedAtUtc { get; set; }

    /// <summary>When the visit ended (UTC). Null if still open.</summary>
    [Display(Name = "Ended At")]
    public DateTime? EndedAtUtc { get; set; }

    /// <summary>Last seen timestamp (readonly, informational).</summary>
    public DateTime LastSeenAtUtc { get; set; }

    // === Editable Location Fields ===

    /// <summary>Latitude of the visit location.</summary>
    [Required]
    [Range(-90, 90)]
    public double Latitude { get; set; }

    /// <summary>Longitude of the visit location.</summary>
    [Required]
    [Range(-180, 180)]
    public double Longitude { get; set; }

    // === Editable Appearance Fields ===

    /// <summary>Icon name for the marker.</summary>
    [Display(Name = "Icon")]
    public string? IconNameSnapshot { get; set; }

    /// <summary>Marker color class (e.g., "bg-blue").</summary>
    [Display(Name = "Marker Color")]
    public string? MarkerColorSnapshot { get; set; }

    // === Editable Notes ===

    /// <summary>Rich-text notes for this visit.</summary>
    [Display(Name = "Notes")]
    public string? NotesHtml { get; set; }

    // === UI Support ===

    /// <summary>Available icon options for dropdown.</summary>
    public List<SelectListItem> IconOptions { get; set; } = new();

    /// <summary>Available color options for dropdown.</summary>
    public List<SelectListItem> ColorOptions { get; set; } = new();

    /// <summary>Return URL for navigation after save.</summary>
    public string? ReturnUrl { get; set; }

    // === Computed Properties ===

    /// <summary>Calculated dwell time if visit has ended.</summary>
    public TimeSpan? DwellTime => EndedAtUtc.HasValue
        ? EndedAtUtc.Value - ArrivedAtUtc
        : null;

    /// <summary>Whether the visit is still open (no end time).</summary>
    public bool IsOpen => !EndedAtUtc.HasValue;
}
