namespace Wayfarer.Models.ViewModels
{
    public class ChangePasswordViewModel
    {
        public string UserId { get; set; }

        public string? UserName { get; set; }

        public string? DisplayName { get; set; }

        public string NewPassword { get; set; }
        public string ConfirmPassword { get; set; }
    }
}
