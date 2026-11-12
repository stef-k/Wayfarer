namespace Wayfarer.Models;

/// <summary>Private durable comparison, owned by its independent destination. Completed rows contain no comparison payload.</summary>
public sealed class TripComposition
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public string UserId { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    /// <summary>Captured identities are private hints, not foreign keys or live acquisition authority.</summary>
    public Guid? SourceAId { get; set; }
    public Guid? SourceBId { get; set; }
    public string? SnapshotA { get; set; }
    public string? SnapshotB { get; set; }
    /// <summary>Initial included identities; current membership determines removed decisions on read.</summary>
    public string? Mappings { get; set; }
}

/// <summary>Lightweight owner-private ancestry; no content, account identity or compulsory live source relationship.</summary>
public sealed class TripOrigin
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public Guid SourceTripId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? ChangeNote { get; set; }
}
