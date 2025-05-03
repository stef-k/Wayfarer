using System.ComponentModel.DataAnnotations;
using Wayfarer.Models.Enums;

namespace Wayfarer.Models.ViewModels;

public class LocationImportUploadViewModel
{
    [Required(ErrorMessage = "Please select a file.")]
    public IFormFile File { get; set; }
    
    [Required(ErrorMessage = "Please select a valid file type.")]
    public LocationImportFileType? FileType { get; set; }
}