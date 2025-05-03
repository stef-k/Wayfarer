using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models.ViewModels
{
    public class AddLocationViewModel
    {
        public int? Id { get; set; } // For editing  

        [Required(ErrorMessage = "Please select an activity type.")]
        public int? SelectedActivityId { get; set; } // Maps to ActivityTypeId

        [BindNever]
        public List<SelectListItem>? ActivityTypes { get; set; } // For dropdown in the view

        [Required]
        public double Latitude { get; set; } // Used to construct Point

        [Required]
        public double Longitude { get; set; } // Used to construct Point

        public string? Address { get; set; }
        public string? FullAddress { get; set; }
        public string? AddressNumber { get; set; }
        public string? StreetName { get; set; }
        public string? PostCode { get; set; }
        public string? Place { get; set; }
        public string? Region { get; set; }
        public string? Country { get; set; }

        public DateTime LocalTimestamp { get; set; }

        public string? TimeZoneId { get; set; }

        public double? Accuracy { get; set; }
        public double? Altitude { get; set; }
        public double? Speed { get; set; }
        public string? Notes { get; set; }

        [BindNever]
        public string? UserId { get; set; } // Will be set in the controller
    }

}