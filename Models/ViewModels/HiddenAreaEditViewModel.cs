using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models.ViewModels;

public class HiddenAreaEditViewModel
{
    public int Id { get; set; }  // Needed for identifying the entity being edited

    [Required]
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    [Required]
    public string AreaWKT { get; set; } = string.Empty;
}
