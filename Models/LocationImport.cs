using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Wayfarer.Models.Enums;

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
}
