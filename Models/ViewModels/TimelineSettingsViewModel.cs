namespace Wayfarer.Models.ViewModels
{
    public class TimelineSettingsViewModel
    {
        private string? _timelineTitle;

        public bool IsTimelinePublic { get; set; }

        /// <summary>
        /// Gets the visible fallback title shown when the optional title is cleared.
        /// </summary>
        public string DefaultTimelineTitle { get; set; } = string.Empty;

        [System.ComponentModel.DataAnnotations.StringLength(80, ErrorMessage = "Timeline title must be 80 characters or fewer.")]
        public string? TimelineTitle
        {
            get => _timelineTitle;
            set => _timelineTitle = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        // Threshold options like "Up to 2 hours before now"
        public string? PublicTimelineTimeThreshold { get; set; }

        public string CustomThreshold { get; set; } = string.Empty;

        /// <summary>
        /// Creates settings for the authenticated timeline owner.
        /// </summary>
        public static TimelineSettingsViewModel FromUser(ApplicationUser user) => new()
        {
            IsTimelinePublic = user.IsTimelinePublic,
            DefaultTimelineTitle = user.ResolveDefaultTimelineTitle(),
            TimelineTitle = user.TimelineTitle,
            PublicTimelineTimeThreshold = user.PublicTimelineTimeThreshold
        };
    }
}
