// CreateUserViewModel.cs

using Microsoft.AspNetCore.Mvc.Rendering;
using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models.ViewModels
{
    public class CreateUserViewModel
    {
        [Required]
        public string UserName { get; set; }

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; }

        [Required]
        public string DisplayName { get; set; }

        [Required(ErrorMessage = "Please select a role.")]
        public string Role { get; set; }

        public bool IsActive { get; set; } = true;

        public bool IsProtected { get; set; } = false;

        public SelectList Roles { get; set; }
    }
}
