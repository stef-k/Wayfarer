namespace Wayfarer.Models.ViewModels
{
    public class UserViewModel
    {
        public string Id { get; set; } = string.Empty;
        public bool IsCurrentUser { get; set; }
        public string Username { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        /// <summary>Gets or sets the persisted role names, before display translation.</summary>
        public IList<string> Roles { get; set; } = [];
        public bool IsActive { get; set; }
        public bool IsProtected { get; set; }
    }

}
