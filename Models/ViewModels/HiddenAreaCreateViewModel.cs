using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models.ViewModels;

/// <summary>Accepts the name, description and invariant polygon from the create form.</summary>
public class HiddenAreaCreateViewModel
{
    [Required(ErrorMessage = "HiddenAreas.NameRequired")]
    public string Name { get; set; } = string.Empty;
    [Required(AllowEmptyStrings = true, ErrorMessage = "HiddenAreas.DescriptionRequired")]
    public string Description { get; set; } = string.Empty;

    [Required(ErrorMessage = "HiddenAreas.DrawRequired")]
    public string AreaWKT { get; set; } = string.Empty;
}
