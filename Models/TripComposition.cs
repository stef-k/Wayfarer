namespace Wayfarer.Models;

/// <summary>Private durable comparison, owned by its independent destination. Completed rows contain no comparison payload.</summary>
public sealed class TripComposition
{
    /// <summary>Stable private identity, independent of HTTP requests.</summary>
    public Guid Id { get; set; }
    /// <summary>Independent destination owning this record and its cascade lifetime.</summary>
    public Guid TripId { get; set; }
    /// <summary>Active destination owner; account erasure cascades this private workspace.</summary>
    public string UserId { get; set; } = null!;
    /// <summary>Fresh server creation time.</summary>
    public DateTime CreatedAt { get; set; }
    /// <summary>Null while active; completed comparisons contain no comparison payload.</summary>
    public DateTime? FinishedAt { get; set; }
    /// <summary>Captured identities are private hints, not foreign keys or live acquisition authority.</summary>
    public Guid? SourceAId { get; set; }
    /// <summary>Second captured source hint, retained only while unfinished.</summary>
    public Guid? SourceBId { get; set; }
    /// <summary>Immutable authorized own-base graph and private planning.</summary>
    public string? SnapshotA { get; set; }
    /// <summary>Immutable authorized comparison graph; foreign planning is never serialized.</summary>
    public string? SnapshotB { get; set; }
    /// <summary>Type-scoped A/B mappings and explicit reuse, separate, skip and destination-removed product decisions; not request histories.</summary>
    public string? Mappings { get; set; }
}

/// <summary>Lightweight owner-private ancestry; no content, account identity or compulsory live source relationship.</summary>
public sealed class TripOrigin
{
    /// <summary>Stable private identity, independent of HTTP requests.</summary>
    public Guid Id { get; set; }
    /// <summary>Independent destination owning this record and its cascade lifetime.</summary>
    public Guid TripId { get; set; }
    /// <summary>Historical scalar identity, never a source deletion cascade or live authority.</summary>
    public Guid SourceTripId { get; set; }
    /// <summary>Fresh server creation time.</summary>
    public DateTime CreatedAt { get; set; }
    /// <summary>Optional owner-private description; never a public credit.</summary>
    public string? ChangeNote { get; set; }
}
