namespace Wayfarer.Models.ViewModels
{
    public class TimelineSettingsViewModel
    {
        public bool IsTimelinePublic { get; set; }

        // Threshold options like "Up to 2 hours before now"
        public string? PublicTimelineTimeThreshold { get; set; }

        public string CustomThreshold { get; set; } = string.Empty;
    }
}
