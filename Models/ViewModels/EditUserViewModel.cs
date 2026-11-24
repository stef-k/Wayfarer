using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models.ViewModels
{
    public class EditUserViewModel
    {
        public string Id { get; set; } = string.Empty;
        public bool IsCurrentUser { get; set; }
        [Required(AllowEmptyStrings = true, ErrorMessage = "Accounts.Validation.UsernameRequired")]
        public string UserName { get; set; } = string.Empty;
        [Required(AllowEmptyStrings = true, ErrorMessage = "Accounts.Validation.DisplayNameRequired")]
        public string DisplayName { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public bool IsProtected { get; set; }
        public string Role { get; set; } = string.Empty; // Single role instead of List<string>
    }
}
