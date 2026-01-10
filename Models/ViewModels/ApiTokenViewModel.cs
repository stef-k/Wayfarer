namespace Wayfarer.Models.ViewModels
{
    /// <summary>
    /// View model for the API Token management page.
    /// Contains user information and tokens.
    /// </summary>
    public class ApiTokenViewModel
    {
        public string UserId { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public List<ApiToken> Tokens { get; set; } = new();
    }
}
