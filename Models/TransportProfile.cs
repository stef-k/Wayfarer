using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models;

/// <summary>
/// Defines one administrator-managed transport choice and its planning assumption.
/// </summary>
public sealed class TransportProfile
{
    /// <summary>Gets or sets the stable database identity.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the immutable normalized API and interchange key.</summary>
    [Required, StringLength(80)]
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the user-facing label.</summary>
    [Required, StringLength(120)]
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the presentation category.</summary>
    [Required, StringLength(80)]
    public string Category { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional positive average planning speed.</summary>
    public double? PlanningSpeedKmh { get; set; }

    /// <summary>Gets or sets the deterministic display order.</summary>
    public int SortOrder { get; set; }

    /// <summary>Gets or sets whether the profile may be selected for new segments.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Gets or sets the optional administrator-facing planning description.</summary>
    [StringLength(500)]
    public string? Description { get; set; }

    /// <summary>Gets or sets whether this record belongs to the approved starter catalog.</summary>
    public bool IsSeeded { get; set; }

    /// <summary>Gets the PostgreSQL transaction identifier used for optimistic concurrency.</summary>
    public uint RowVersion { get; private set; }

    /// <summary>Normalizes a durable key for storage and comparison.</summary>
    public static string NormalizeKey(string key) => key.Trim().ToLowerInvariant();
}
