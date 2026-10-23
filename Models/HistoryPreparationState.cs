namespace Wayfarer.Models;

/// <summary>Single-generation, independently resumable time or text preparation; never canonical data authority.</summary>
public sealed class HistoryPreparationState
{
    /// <summary>Stable primary key: time or text.</summary>
    public required string Kind { get; set; }
    /// <summary>Pinned binary-supported target rules.</summary>
    public required string TargetVersion { get; set; }
    /// <summary>Activated only after a complete all-owner verification.</summary>
    public string? ActiveVersion { get; set; }
    /// <summary>False throughout selected-generation maintenance, including a same-version rebuild.</summary>
    public bool Ready { get; set; }
    /// <summary>Qualified writer contract recorded by the preparation binary.</summary>
    public string? WriterVersion { get; set; }
    /// <summary>True while a controlled full pass remains, including same-version rebuilds.</summary>
    public bool Scanning { get; set; }
    /// <summary>Committed stable-ID checkpoint; never sufficient proof of completion.</summary>
    public int LastId { get; set; }
    /// <summary>Durable rows examined in this preparation, including forced rebuild work.</summary>
    public long Examined { get; set; }
    /// <summary>Durable rows prepared in this generation.</summary>
    public long Prepared { get; set; }
    /// <summary>Safe actionable category; no SQL, source values or credentials.</summary>
    public string? LastFailure { get; set; }
    /// <summary>Last committed progress or readiness transition.</summary>
    public DateTime UpdatedAtUtc { get; set; }
}
