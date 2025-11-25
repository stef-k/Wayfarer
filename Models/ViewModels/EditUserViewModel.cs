namespace Wayfarer.Models.ViewModels
{
    public class EditUserViewModel
    {
        public string Id { get; set; } = string.Empty;
        public bool IsCurrentUser { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public bool IsProtected { get; set; }
        public string Role { get; set; } = string.Empty; // Single role instead of List<string>
    }
}
