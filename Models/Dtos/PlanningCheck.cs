using Wayfarer.Models.Planning;

namespace Wayfarer.Models.Dtos;

/// <summary>One bounded latest private setup. These are interpretations, never mutation originals or itinerary facts.</summary>
public sealed record PlanningCheckSetup
{
    /// <summary>Null inventories the whole current Trip; a list is an explicit temporary selection.</summary>
    public IReadOnlyList<string>? Selection { get; init; }
    /// <summary>Explicit premise, revalidated when selected participants change.</summary>
    public bool SingleTraveller { get; init; }
    /// <summary>Participant evidence for the premise, not an authorization token.</summary>
    public string? TravellerDependency { get; init; }
    /// <summary>Check-only entry meanings, frames, dwell and parent-window choices.</summary>
    public IReadOnlyDictionary<string, PlanningCheckEntryInput> Entries { get; init; } = new Dictionary<string, PlanningCheckEntryInput>();
    /// <summary>Explicit movement pairs; manual display order never implies a schedule.</summary>
    public IReadOnlyList<PlanningCheckTransfer> Transfers { get; init; } = [];
    /// <summary>Explicit incompatible-location assessments for presence pairs.</summary>
    public IReadOnlyDictionary<string, string?> Incompatible { get; init; } = new Dictionary<string, string?>();
}

/// <summary>Saved values win; missing facts can be interpreted for this check without changing an entry.</summary>
public sealed record PlanningCheckEntryInput(string? Kind = null, string? Zone = null, int? AllocationMinutes = null,
    IReadOnlyList<string>? Context = null, string? Dependency = null, string? ContextDependency = null,
    IReadOnlyDictionary<string, int?>? Occurrences = null, string? OccurrenceDependency = null);

/// <summary>Explicit source and inclusion treatment. No provider is called and no minimum connection is invented.</summary>
public sealed record PlanningCheckTransfer(string From, string To, Guid? SegmentId = null, double? EstimateMinutes = null,
    bool Relevant = false, string Allowance = "unknown", double? AllowanceMinutes = null,
    string Connection = "unknown", double? ConnectionMinutes = null, string? Dependency = null,
    PlanningValue? WhatIfRelease = null, DateTimeOffset? WhatIfAt = null, string? WhatIfDependency = null);

/// <summary>Every saved entry remains in inventory, including explicit exclusions and unresolved interpretations.</summary>
public sealed record PlanningCheckItem(string Key, string Kind, Guid TargetId, Guid ParentId, string Name, PlanningEntry Entry,
    bool Selected, IReadOnlyList<string> ParentWindows, string Dependency);

/// <summary>A dismissible displayed assessment; dismissal never changes scope, counts or later computation.</summary>
public sealed record PlanningCheckFinding(string Id, string Check, IReadOnlyList<string> Entries, PlanningComparison Result);

/// <summary>Fresh authorized result plus revalidated latest inputs; no persistence, export or mutation authority.</summary>
public sealed record PlanningCheckResult(string Contract, string OwnerId, Guid TripId, string ZoneRulesVersion,
    IReadOnlyList<PlanningCheckItem> Inventory, IReadOnlyList<PlanningCheckFinding> Findings, PlanningCheckSetup Setup,
    IReadOnlyList<string> Reused, IReadOnlyList<string> Invalidated);
