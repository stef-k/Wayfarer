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
        /// Gets or sets the explicit acknowledgement required for live public sharing.
        /// </summary>
        public bool ConfirmLivePublicTimeline { get; set; }

        /// <summary>
        /// Gets the owner-facing status derived from the persisted public eligibility state.
        /// </summary>
        public string PublicTimelineStatus { get; set; } = string.Empty;

        /// <summary>
        /// Gets whether a stored public threshold is inconsistent and needs replacement.
        /// </summary>
        public bool HasInvalidPublicTimelineThreshold { get; set; }

        /// <summary>
        /// Creates settings for the authenticated timeline owner.
        /// </summary>
        public static TimelineSettingsViewModel FromUser(ApplicationUser user)
        {
            var eligibility = Wayfarer.Services.PublicTimelineEligibilityResolver.Resolve(user);
            return new()
            {
                IsTimelinePublic = user.IsTimelinePublic,
                DefaultTimelineTitle = user.ResolveDefaultTimelineTitle(),
                TimelineTitle = user.TimelineTitle,
                PublicTimelineTimeThreshold = eligibility.IsThresholdValid ? user.PublicTimelineTimeThreshold : string.Empty,
                PublicTimelineStatus = eligibility.IsEffectivelyPublic
                    ? eligibility.IsLive
                        ? "Public timeline: live"
                        : $"Public timeline: delayed by {eligibility.Delay}"
                    : user.IsTimelinePublic
                        ? "Public timeline unavailable until a valid threshold is selected and saved."
                        : "Timeline is not publicly available.",
                HasInvalidPublicTimelineThreshold = user.IsTimelinePublic && !eligibility.IsThresholdValid
            };
        }
    }
}
