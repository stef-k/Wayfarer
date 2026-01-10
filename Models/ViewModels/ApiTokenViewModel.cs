namespace Wayfarer.Models.ViewModels
{
    /// <summary>
    /// View model for the API Token management page.
    /// Contains user information, tokens, and location logging threshold settings.
    /// </summary>
    public class ApiTokenViewModel
    {
        public string UserId { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public List<ApiToken> Tokens { get; set; } = new();

        /// <summary>
        /// Location time threshold in minutes. Locations logged within this time window
        /// of the previous location are skipped.
        /// </summary>
        public int LocationTimeThresholdMinutes { get; set; }

        /// <summary>
        /// Location distance threshold in meters. Locations logged within this distance
        /// of the previous location are skipped.
        /// </summary>
        public int LocationDistanceThresholdMeters { get; set; }
    }
}
