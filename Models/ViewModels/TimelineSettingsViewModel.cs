using Wayfarer.Localization;
using Wayfarer.Services;

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

        [System.ComponentModel.DataAnnotations.StringLength(80, ErrorMessage = "Settings.TitleLength")]
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
        public static TimelineSettingsViewModel FromUser(ApplicationUser user, WebMessages text)
        {
            var eligibility = PublicTimelineEligibilityResolver.Resolve(user);
            var threshold = eligibility.IsThresholdValid ? user.PublicTimelineTimeThreshold : string.Empty;
            var model = new TimelineSettingsViewModel
            {
                IsTimelinePublic = user.IsTimelinePublic,
                TimelineTitle = user.TimelineTitle,
                PublicTimelineTimeThreshold = threshold,
                CustomThreshold = threshold is null or "" or "now" or "1h" or "1d" or "1w" or "1m" or "1y"
                    ? string.Empty : threshold,
                ConfirmLivePublicTimeline = eligibility.IsEffectivelyPublic && eligibility.IsLive
            };
            model.SetPersistedPresentation(user, eligibility, text);
            return model;
        }

        /// <summary>Displays saved availability independently of any submitted draft.</summary>
        internal void SetPersistedPresentation(ApplicationUser user, PublicTimelineEligibility eligibility, WebMessages text)
        {
            var displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName;
            DefaultTimelineTitle = text["PublicTimeline.Heading", displayName ?? string.Empty];
            HasInvalidPublicTimelineThreshold = user.IsTimelinePublic && !eligibility.IsThresholdValid;
            PublicTimelineStatus = eligibility.IsEffectivelyPublic
                ? eligibility.IsLive
                    ? text["Settings.StatusLive"]
                    : text["Settings.StatusDelayed", eligibility.Delay?.ToString("g", text.DisplayCulture)]
                : text[HasInvalidPublicTimelineThreshold ? "Settings.StatusInvalid" : "Settings.StatusPrivate"];
        }
    }
}
