using System.ComponentModel.DataAnnotations;
using Wayfarer.Models.Enums;

namespace Wayfarer.Models.ViewModels;

public class LocationImportUploadViewModel
{
    /// <summary>The uploaded location-history file.</summary>
    [Required(ErrorMessage = "Transfers.RequiredFile")]
    public IFormFile File { get; set; } = default!;
    
    /// <summary>The selected parser format, transmitted as its existing enum value.</summary>
    [Required(ErrorMessage = "Transfers.InvalidFormat")]
    public LocationImportFileType? FileType { get; set; }

    /// <summary>Whether remaining blank-address Locations should continue through the durable workflow.</summary>
    public bool EnrichmentRequested { get; set; }
}
