using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Wayfarer.Models.LocationEnrichment;

/// <summary>Names the complete durable state machine for one user's enrichment work.</summary>
public enum LocationEnrichmentState
{
    Idle, Scheduled, Running, PausedByUser, PausedByBudget, PausedByAuthority,
    BackingOff, Completed, Cancelled, Failed
}

/// <summary>Stores bounded product outcomes without retaining provider or exception content.</summary>
public enum LocationEnrichmentOutcome
{
    None, NoCandidates, BudgetExhausted, AuthorityUnavailable, RetryableFailure,
    InvalidCoordinates, NoResult, AttemptLimit, DataFailure
}

/// <summary>PostgreSQL authority for one user-controlled, restart-safe enrichment workflow.</summary>
public sealed class LocationEnrichmentWorkflow
{
    private static readonly HashSet<LocationEnrichmentState> TerminalStates =
        [LocationEnrichmentState.Completed, LocationEnrichmentState.Cancelled, LocationEnrichmentState.Failed];

    [Key, MaxLength(450)]
    public string UserId { get; private set; } = string.Empty;
    public Guid SchedulerId { get; private set; }
    public LocationEnrichmentState State { get; private set; } = LocationEnrichmentState.Idle;
    public bool IntentEnabled { get; private set; }
    public int Epoch { get; private set; }
    public LocationEnrichmentOutcome Outcome { get; private set; }
    public int ProcessedCount { get; private set; }
    public int EnrichedCount { get; private set; }
    public int SkippedCount { get; private set; }
    public int RetryableDeferredCount { get; private set; }
    public int PermanentlyDeferredCount { get; private set; }
    public int RemainingEligibleCount { get; private set; }
    public int AdmittedUsageCount { get; private set; }
    public DateTime? NextEligibleAtUtc { get; private set; }
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public uint Version { get; private set; }
    public ApplicationUser? User { get; private set; }
    public ICollection<LocationEnrichmentAttempt> Attempts { get; } = [];

    private LocationEnrichmentWorkflow() { }

    /// <summary>Creates the retained one-per-user authority without enabling provider contact.</summary>
    public static LocationEnrichmentWorkflow Create(string userId, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        EnsureUtc(nowUtc);
        return new() { UserId = userId, SchedulerId = Guid.NewGuid(), CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc };
    }

    /// <summary>Enables a scheduled run idempotently and advances only a new terminal epoch.</summary>
    public void Start(DateTime nowUtc)
    {
        EnsureUtc(nowUtc);
        if (State is LocationEnrichmentState.Scheduled or LocationEnrichmentState.Running || IntentEnabled)
            return;
        if (Epoch == 0 || TerminalStates.Contains(State)) Epoch++;
        IntentEnabled = true;
        State = LocationEnrichmentState.Scheduled;
        Outcome = LocationEnrichmentOutcome.None;
        NextEligibleAtUtc = nowUtc;
        StartedAtUtc = nowUtc;
        CompletedAtUtc = null;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Persists user pause intent before any scheduler interruption.</summary>
    public void Pause(DateTime nowUtc)
    {
        EnsureUtc(nowUtc);
        if (State == LocationEnrichmentState.Cancelled) return;
        if (State != LocationEnrichmentState.PausedByUser) Epoch++;
        IntentEnabled = false;
        State = LocationEnrichmentState.PausedByUser;
        NextEligibleAtUtc = null;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Attempts a user pause and returns a bounded conflict rather than throwing.</summary>
    public bool TryPause(DateTime nowUtc, out string? reason)
    {
        EnsureUtc(nowUtc);
        if (State is LocationEnrichmentState.Idle or LocationEnrichmentState.Completed
            or LocationEnrichmentState.Cancelled or LocationEnrichmentState.Failed)
        { reason = "invalid-state"; return false; }
        if (State == LocationEnrichmentState.PausedByUser) { reason = null; return true; }
        Pause(nowUtc); reason = null; return true;
    }

    /// <summary>Resumes the same nonterminal epoch idempotently.</summary>
    public void Resume(DateTime nowUtc)
    {
        EnsureUtc(nowUtc);
        if (IntentEnabled && State == LocationEnrichmentState.Scheduled) return;
        if (TerminalStates.Contains(State)) return;
        IntentEnabled = true;
        State = LocationEnrichmentState.Scheduled;
        Outcome = LocationEnrichmentOutcome.None;
        NextEligibleAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Attempts a valid user resume after current provider authority has been checked.</summary>
    public bool TryResume(DateTime nowUtc, bool authorityAvailable, out string? reason)
    {
        EnsureUtc(nowUtc);
        if (State == LocationEnrichmentState.Scheduled && IntentEnabled) { reason = null; return true; }
        if (State != LocationEnrichmentState.PausedByUser || !authorityAvailable)
        { reason = authorityAvailable ? "invalid-state" : "authority-unavailable"; return false; }
        Resume(nowUtc); reason = null; return true;
    }

    /// <summary>Persists a run-wide authority pause without erasing user opt-in or durable data.</summary>
    public void PauseForAuthority(LocationEnrichmentOutcome outcome, DateTime nowUtc)
    {
        EnsureUtc(nowUtc);
        State = LocationEnrichmentState.PausedByAuthority;
        Outcome = outcome;
        NextEligibleAtUtc = null;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Advances the epoch once for an explicit deferred-attempt retry request.</summary>
    public bool RetryDeferred(DateTime nowUtc)
    {
        EnsureUtc(nowUtc);
        if (State is LocationEnrichmentState.Running or LocationEnrichmentState.Scheduled
            or LocationEnrichmentState.BackingOff or LocationEnrichmentState.PausedByBudget) return false;
        Epoch++;
        IntentEnabled = true;
        State = LocationEnrichmentState.Scheduled;
        Outcome = LocationEnrichmentOutcome.None;
        NextEligibleAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
        return true;
    }

    /// <summary>Persists terminal cancellation without clearing results, attempts, or usage.</summary>
    public void Cancel(DateTime nowUtc)
    {
        EnsureUtc(nowUtc);
        if (State == LocationEnrichmentState.Cancelled) return;
        Epoch++;
        IntentEnabled = false;
        State = LocationEnrichmentState.Cancelled;
        NextEligibleAtUtc = null;
        CompletedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Records committed bounded progress; counters are cumulative across epochs.</summary>
    public void RecordBatch(int processed, int enriched, int retryableDeferred, int permanentlyDeferred,
        int admittedUsage, DateTime nowUtc)
    {
        EnsureUtc(nowUtc);
        if (processed < 0 || enriched < 0 || retryableDeferred < 0 || permanentlyDeferred < 0 || admittedUsage < 0)
            throw new ArgumentOutOfRangeException(nameof(processed));
        ProcessedCount += processed;
        EnrichedCount += enriched;
        RetryableDeferredCount += retryableDeferred;
        PermanentlyDeferredCount += permanentlyDeferred;
        AdmittedUsageCount += admittedUsage;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Applies one validated terminal transition while retaining durable progress.</summary>
    public void TransitionToTerminal(LocationEnrichmentState state, LocationEnrichmentOutcome outcome, DateTime nowUtc)
    {
        EnsureUtc(nowUtc);
        if (!TerminalStates.Contains(state)) throw new ArgumentOutOfRangeException(nameof(state));
        State = state;
        IntentEnabled = false;
        Outcome = outcome;
        NextEligibleAtUtc = null;
        CompletedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Claims a current scheduled epoch immediately before bounded worker entry.</summary>
    public bool TryClaim(int epoch, DateTime nowUtc)
    {
        EnsureUtc(nowUtc);
        if (!IntentEnabled || State != LocationEnrichmentState.Scheduled || Epoch != epoch) return false;
        State = LocationEnrichmentState.Running;
        NextEligibleAtUtc = null;
        UpdatedAtUtc = nowUtc;
        return true;
    }

    /// <summary>Recovers an abandoned running observation to scheduled relational intent.</summary>
    public void RecoverRunning(DateTime nowUtc)
    {
        EnsureUtc(nowUtc);
        if (State != LocationEnrichmentState.Running) return;
        State = IntentEnabled ? LocationEnrichmentState.Scheduled : LocationEnrichmentState.PausedByUser;
        NextEligibleAtUtc = IntentEnabled ? nowUtc : null;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Persists the next authoritative state after one committed bounded batch.</summary>
    public void ContinueAs(LocationEnrichmentState state, LocationEnrichmentOutcome outcome,
        DateTime? nextEligibleAtUtc, DateTime nowUtc)
    {
        EnsureUtc(nowUtc);
        if (nextEligibleAtUtc.HasValue) EnsureUtc(nextEligibleAtUtc.Value);
        if (state is not (LocationEnrichmentState.Scheduled or LocationEnrichmentState.PausedByBudget
            or LocationEnrichmentState.PausedByAuthority or LocationEnrichmentState.BackingOff))
            throw new ArgumentOutOfRangeException(nameof(state));
        State = state;
        Outcome = outcome;
        NextEligibleAtUtc = nextEligibleAtUtc;
        IntentEnabled = state != LocationEnrichmentState.PausedByAuthority;
        UpdatedAtUtc = nowUtc;
    }

    private static void EnsureUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc) throw new ArgumentException("Workflow timestamps must be UTC.");
    }
}

/// <summary>Maps workflow authority, constraints, concurrency, and due selection.</summary>
public sealed class LocationEnrichmentWorkflowConfiguration : IEntityTypeConfiguration<LocationEnrichmentWorkflow>
{
    public void Configure(EntityTypeBuilder<LocationEnrichmentWorkflow> builder)
    {
        builder.Property(item => item.State).HasConversion<string>().HasMaxLength(32);
        builder.Property(item => item.Outcome).HasConversion<string>().HasMaxLength(32);
        builder.Property(item => item.Version).HasColumnName("xmin").IsRowVersion().ValueGeneratedOnAddOrUpdate();
        builder.HasOne(item => item.User).WithOne().HasForeignKey<LocationEnrichmentWorkflow>(item => item.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(item => new { item.State, item.NextEligibleAtUtc })
            .HasDatabaseName("IX_LocationEnrichmentWorkflow_Due");
        builder.HasIndex(item => item.SchedulerId).IsUnique();
        builder.ToTable(table =>
        {
            table.HasCheckConstraint("CK_LocationEnrichmentWorkflow_Epoch", "\"Epoch\" >= 0");
            table.HasCheckConstraint("CK_LocationEnrichmentWorkflow_Counters",
                "\"ProcessedCount\" >= 0 AND \"EnrichedCount\" >= 0 AND \"SkippedCount\" >= 0 AND \"RetryableDeferredCount\" >= 0 AND \"PermanentlyDeferredCount\" >= 0 AND \"RemainingEligibleCount\" >= 0 AND \"AdmittedUsageCount\" >= 0");
        });
    }
}
