using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models.ViewModels;

/// <summary>Accepts the name, description and invariant polygon from the edit form.</summary>
public class HiddenAreaEditViewModel
{
    public int Id { get; set; }  // Needed for identifying the entity being edited

    [Required(ErrorMessage = "HiddenAreas.NameRequired")]
    public string Name { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = true, ErrorMessage = "HiddenAreas.DescriptionRequired")]
    public string Description { get; set; } = string.Empty;

    [Required(ErrorMessage = "HiddenAreas.DrawRequired")]
    public string AreaWKT { get; set; } = string.Empty;
}
