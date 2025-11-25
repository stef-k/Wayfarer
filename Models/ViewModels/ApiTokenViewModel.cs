namespace Wayfarer.Models.ViewModels
{
    public class ApiTokenViewModel
    {
        public string UserId { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public List<ApiToken> Tokens { get; set; } = new();
    }
}
