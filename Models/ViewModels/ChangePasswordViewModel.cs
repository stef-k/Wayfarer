namespace Wayfarer.Models.ViewModels
{
    public class ChangePasswordViewModel
    {
        public string UserId { get; set; } = string.Empty;

        public string? UserName { get; set; }

        public string? DisplayName { get; set; }

        public string NewPassword { get; set; } = string.Empty;
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
