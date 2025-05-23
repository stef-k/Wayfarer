using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models.ViewModels;

public class HiddenAreaEditViewModel
{
    public int Id { get; set; }  // Needed for identifying the entity being edited

    [Required]
    public string Name { get; set; }

    public string Description { get; set; }

    [Required]
    public string AreaWKT { get; set; }
}