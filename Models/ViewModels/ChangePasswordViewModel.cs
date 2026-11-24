using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models.ViewModels
{
    public class ChangePasswordViewModel
    {
        public string UserId { get; set; } = string.Empty;

        public string? UserName { get; set; }

        public string? DisplayName { get; set; }

        [Required(AllowEmptyStrings = true, ErrorMessage = "Accounts.Validation.PasswordRequired")]
        public string NewPassword { get; set; } = string.Empty;
        [Required(AllowEmptyStrings = true, ErrorMessage = "Accounts.Validation.ConfirmPasswordRequired")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
