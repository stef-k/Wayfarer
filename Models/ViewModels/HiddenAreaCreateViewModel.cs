using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models.ViewModels;

public class HiddenAreaCreateViewModel
{
    [Required]
    public string Name { get; set; }
    public string Description { get; set; }

    [Required]
    public string AreaWKT { get; set; }
}
