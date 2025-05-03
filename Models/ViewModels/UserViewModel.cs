namespace Wayfarer.Models.ViewModels
{
    public class UserViewModel
    {
        public string Id { get; set; }
        public bool IsCurrentUser { get; set; }
        public string Username { get; set; }
        public string DisplayName { get; set; }
        public string Role { get; set; }
        public bool IsActive { get; set; }
        public bool IsProtected { get; set; }
    }

}
