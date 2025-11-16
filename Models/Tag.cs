using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models;

/// <summary>
/// Represents a globally deduplicated tag that can be attached to trips.
/// </summary>
public class Tag
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>Human-friendly tag name (stored as citext for case-insensitive uniqueness).</summary>
    [Required]
    [StringLength(64)]
    public string Name { get; set; } = default!;

    /// <summary>Lowercase ASCII slug derived from <see cref="Name"/>.</summary>
    [Required]
    [StringLength(200)]
    public string Slug { get; set; } = default!;

    /// <summary>Trips that currently reference this tag.</summary>
    public ICollection<Trip> Trips { get; set; } = new List<Trip>();
}
