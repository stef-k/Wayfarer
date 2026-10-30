using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace Wayfarer.Models;

/// <summary>Finite attribution and executor binding, without executable request bodies or Location cascade.</summary>
[Index(nameof(UserId), nameof(BaseId), nameof(ActionId), IsUnique = true)]
[Index(nameof(ReceiptRetainUntilUtc), nameof(UserId), nameof(Id))]
public sealed class LocationMutationOperation
{
    /// <summary>Internal indexed cleanup identity.</summary>
    public long Id { get; set; }
    /// <summary>Authenticated subject; account erasure cascades its receipts.</summary>
    public required string UserId { get; set; }
    /// <summary>Required account relationship supplies deliberate account-erasure cascade.</summary>
    [ForeignKey(nameof(UserId))]
    public ApplicationUser User { get; set; } = null!;
    /// <summary>Target identity stays recoverable after physical deletion.</summary>
    public int LocationId { get; set; }
    /// <summary>Original nullable ingestion key, never invented for keyless rows.</summary>
    public Guid? Uuid { get; set; }
    /// <summary>Signed authority identity.</summary>
    public Guid BaseId { get; set; }
    /// <summary>Client's immutable action identity.</summary>
    public Guid ActionId { get; set; }
    /// <summary>Bound request adapter.</summary>
    public required string Adapter { get; set; }
    /// <summary>Bound update/delete kind.</summary>
    public required string Kind { get; set; }
    /// <summary>Immutable comparison semantics.</summary>
    public required string Normalization { get; set; }
    /// <summary>Seven-frame digest of the original received bytes.</summary>
    public required string Fingerprint { get; set; }
    /// <summary>Nonrenewable signed authority expiry.</summary>
    public DateTime ExpiresAtUtc { get; set; }
    /// <summary>First binding time.</summary>
    public DateTime AcceptedAtUtc { get; set; }
    /// <summary>Fixed executor deadline; expired Pending always projects this completion time.</summary>
    public DateTime ExecuteBeforeUtc { get; set; }
    /// <summary>Cleanup eligibility, never a deadline for client work.</summary>
    public DateTime ReceiptRetainUntilUtc { get; set; }
    /// <summary>Only the first invocation can publish provider output.</summary>
    public Guid ExecutorNonce { get; set; }
    /// <summary>Pending/applied/rejected state.</summary>
    public required string State { get; set; }
    /// <summary>Compact immutable terminal envelope, without HTML or winner data.</summary>
    public string? ResultJson { get; set; }
    /// <summary>Terminal HTTP classification, independent of recovery transport.</summary>
    public int? ResultStatus { get; set; }
}
