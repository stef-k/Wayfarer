using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Areas.Admin.Models;

/// <summary>Allowlisted fields for creating a transport profile.</summary>
public class TransportProfileCreateViewModel : IValidatableObject
{
    /// <summary>Gets or sets the durable normalized key.</summary>
    [Required, StringLength(80)]
    [RegularExpression("^[a-z0-9]+(?:-[a-z0-9]+)*$", ErrorMessage = "Use lowercase letters, numbers, and single hyphens only.")]
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the user-facing label.</summary>
    [Required, StringLength(120)]
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the presentation category.</summary>
    [Required, StringLength(80)]
    public string Category { get; set; } = string.Empty;

    /// <summary>Gets or sets the nullable planning speed.</summary>
    public double? PlanningSpeedKmh { get; set; }

    /// <summary>Gets or sets the deterministic order.</summary>
    public int SortOrder { get; set; }

    /// <summary>Gets or sets whether new segments may select the profile.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Gets or sets the optional administrator description.</summary>
    [StringLength(500)]
    public string? Description { get; set; }

    /// <summary>Validates finite positive planning speeds.</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (PlanningSpeedKmh is double speed && (!double.IsFinite(speed) || speed <= 0))
        {
            yield return new ValidationResult("Planning speed must be a finite positive number.", [nameof(PlanningSpeedKmh)]);
        }
    }
}

/// <summary>Allowlisted mutable fields for editing an existing profile.</summary>
public sealed class TransportProfileEditViewModel : TransportProfileCreateViewModel
{
    /// <summary>Gets or sets the stable identity.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the PostgreSQL optimistic-concurrency token.</summary>
    public uint RowVersion { get; set; }

    /// <summary>Gets or sets explicit confirmation for deactivating a referenced profile.</summary>
    public bool ConfirmDeactivation { get; set; }

    /// <summary>Gets or sets whether the loaded record was active before this edit.</summary>
    public bool WasActive { get; set; }

    /// <summary>Gets the current dependency count displayed before mutation.</summary>
    public int ReferencedSegments { get; set; }

}

/// <summary>Read-only transport-profile row for index and delete confirmation.</summary>
public sealed record TransportProfileRowViewModel(Guid Id, string Key, string Label, string Category, double? PlanningSpeedKmh, int SortOrder, bool IsActive, bool IsSeeded, int ReferencedSegments, uint RowVersion);

/// <summary>Paginated deterministic transport-profile index state.</summary>
public sealed record TransportProfileIndexViewModel(IReadOnlyList<TransportProfileRowViewModel> Items, string Search, int Page, int TotalPages);
