// CreateUserViewModel.cs

using Microsoft.AspNetCore.Mvc.Rendering;
using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models.ViewModels
{
    public class CreateUserViewModel
    {
        [Required(ErrorMessage = "Accounts.Validation.UsernameRequired")]
        public string UserName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Accounts.Validation.PasswordRequired")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Required(ErrorMessage = "Accounts.Validation.DisplayNameRequired")]
        public string DisplayName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Accounts.Validation.SelectRole")]
        public string Role { get; set; } = string.Empty;

        public bool IsActive { get; set; } = true;

        public bool IsProtected { get; set; } = false;

        public SelectList Roles { get; set; } = null!;
    }
}
