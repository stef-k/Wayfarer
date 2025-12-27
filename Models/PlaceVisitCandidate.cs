using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace Wayfarer.Models;

/// <summary>
/// Tracks pre-confirmation hits for visit detection.
/// Used to confirm a visit via multiple consecutive pings within the hit window.
/// Ephemeral: deleted once a PlaceVisitEvent is created or when stale.
/// </summary>
public class PlaceVisitCandidate
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>User identifier from ASP.NET Identity.</summary>
    [BindNever, ValidateNever]
    public string UserId { get; set; } = string.Empty;

    /// <summary>Foreign key to the place being tracked.</summary>
    public Guid PlaceId { get; set; }

    /// <summary>Navigation property to the place.</summary>
    [ValidateNever]
    [JsonIgnore]
    public Place Place { get; set; } = null!;

    /// <summary>UTC timestamp of the first ping within the place radius.</summary>
    public DateTime FirstHitUtc { get; set; }

    /// <summary>UTC timestamp of the most recent ping within the place radius.</summary>
    public DateTime LastHitUtc { get; set; }

    /// <summary>
    /// Number of consecutive pings within the hit window.
    /// When this reaches VisitedRequiredHits, a PlaceVisitEvent is created.
    /// </summary>
    public int ConsecutiveHits { get; set; }
}
