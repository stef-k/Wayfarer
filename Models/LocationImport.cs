using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Wayfarer.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Wayfarer.Models;

public class ImportStatus
{
    // Define the statuses as static readonly fields
    public static readonly ImportStatus InProgress = new ImportStatus("In Progress");
    public static readonly ImportStatus Stopping = new ImportStatus("Stopping");
    public static readonly ImportStatus Stopped = new ImportStatus("Stopped");
    public static readonly ImportStatus Completed = new ImportStatus("Completed");
    public static readonly ImportStatus Failed = new ImportStatus("Failed");

    // Private constructor to ensure only the predefined values can be used
    public string Value { get; }

    public ImportStatus(string value)
    {
        Value = value;
    }

    // Optionally override ToString() for easy display
    public override string ToString() => Value;

    // You can also add comparison operators
    public static bool operator ==(ImportStatus left, ImportStatus right) => left?.Value == right?.Value;
    public static bool operator !=(ImportStatus left, ImportStatus right) => left?.Value != right?.Value;

    // Override Equals and GetHashCode for better comparisons
    public override bool Equals(object? obj) => obj is ImportStatus status && status.Value == Value;
    public override int GetHashCode() => Value.GetHashCode();
}

/// <summary>
/// Represents an import of a location data file (Google timeline, gpx, geojson, etc)
/// </summary>
public class LocationImport
{
    [Key]
    public int Id { get; set; }
    
    // Foreign Key
    [Required]
    public required string UserId { get; set; }

    // Navigation property
    [ForeignKey("UserId")]
    public virtual ApplicationUser? User { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    public required  LocationImportFileType FileType { get; set; }

    public required int TotalRecords { get; set; } = 0;

    public string? LastImportedRecord { get; set; } = "N/A";
    public required string FilePath { get; set; } = string.Empty;
    public required int LastProcessedIndex { get; set; } = 0;
    // Status (Pending, InProgress, Completed, Failed)
    public ImportStatus Status { get; set; } = ImportStatus.Stopped;  // Default to 'Pending' status
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Count of locations skipped during import due to deduplication.
    /// </summary>
    public int SkippedDuplicates { get; set; }

    /// <summary>Whether this import may hand remaining enrichment to the user's durable workflow.</summary>
    public bool EnrichmentRequested { get; set; }

    /// <summary>UTC time at which the user explicitly opted into durable enrichment.</summary>
    public DateTime? EnrichmentRequestedAtUtc { get; set; }

    /// <summary>Bounded reason why inline enrichment stopped while import persistence continued.</summary>
    [MaxLength(32)]
    public string? EnrichmentPauseReason { get; set; }

    /// <summary>Last bounded estimate of imported rows still eligible for enrichment.</summary>
    public int RemainingEnrichmentCount { get; set; }

    /// <summary>Monotonic authority generation embedded in the Quartz projection.</summary>
    public int ExecutionEpoch { get; set; }

    /// <summary>Whether durable intent still needs to be projected to Quartz.</summary>
    public bool ProjectionPending { get; set; }

    /// <summary>UTC user stop intent; null means the current epoch may execute.</summary>
    public DateTime? StopRequestedAtUtc { get; set; }

    /// <summary>UTC terminal-history deletion intent awaiting external cleanup.</summary>
    public DateTime? DeletionRequestedAtUtc { get; set; }

    /// <summary>PostgreSQL optimistic concurrency token.</summary>
    public uint Version { get; private set; }
}

/// <summary>Constrains bounded enrichment handoff facts retained with an import.</summary>
public sealed class LocationImportConfiguration : IEntityTypeConfiguration<LocationImport>
{
    public void Configure(EntityTypeBuilder<LocationImport> builder)
    {
        builder.Property(item => item.Version).HasColumnName("xmin").IsRowVersion().ValueGeneratedOnAddOrUpdate();
        builder.ToTable(table =>
        {
            table.HasCheckConstraint("CK_LocationImport_RemainingEnrichment", "\"RemainingEnrichmentCount\" >= 0");
            table.HasCheckConstraint("CK_LocationImport_EnrichmentPauseReason",
                "\"EnrichmentPauseReason\" IS NULL OR \"EnrichmentPauseReason\" IN "
                + "('CredentialRequired','NoProviderSelected','ConsentRequired','Unauthorized','VerificationRequired','Exhausted','StaleAuthority')");
            table.HasCheckConstraint("CK_LocationImport_ExecutionEpoch", "\"ExecutionEpoch\" >= 0");
            table.HasCheckConstraint("CK_LocationImport_LifecycleState",
                "((\"Status\" = 'In Progress' AND \"StopRequestedAtUtc\" IS NULL "
                + "AND \"DeletionRequestedAtUtc\" IS NULL) OR "
                + "(\"Status\" = 'Stopping' AND \"StopRequestedAtUtc\" IS NOT NULL "
                + "AND \"DeletionRequestedAtUtc\" IS NULL AND \"ProjectionPending\") OR "
                + "(\"Status\" = 'Stopped' AND NOT \"ProjectionPending\") OR "
                + "(\"Status\" IN ('Completed','Failed') AND \"StopRequestedAtUtc\" IS NULL "
                + "AND NOT \"ProjectionPending\")) IS TRUE");
        });
    }
}
