namespace Wayfarer.Models.ViewModels
{
    public class ApiTokenViewModel
    {
        public string UserId { get; set; }
        public string UserName { get; set; }
        public List<ApiToken> Tokens { get; set; }
    }
}