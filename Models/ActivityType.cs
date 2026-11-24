using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models
{
    public class ActivityType
    {
        public int Id { get; set; }

        /// <summary>Gets or sets the editable Activity name.</summary>
        [Required(AllowEmptyStrings = true, ErrorMessage = "Catalogs.Validation.ActivityNameRequired")]
        public required string Name { get; set; }

        // Optional field for more details about the activity type
        public string? Description { get; set; }
    }

}
