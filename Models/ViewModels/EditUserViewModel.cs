namespace Wayfarer.Models.ViewModels
{
    public class EditUserViewModel
    {
        public string Id { get; set; }
        public bool IsCurrentUser { get; set; }
        public string UserName { get; set; }
        public string DisplayName { get; set; }
        public bool IsActive { get; set; }
        public bool IsProtected { get; set; }
        public string Role { get; set; } // Single role instead of List<string>
    }
}
