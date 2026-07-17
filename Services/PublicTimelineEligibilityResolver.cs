using Wayfarer.Models;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>
/// Resolves whether a persisted public timeline threshold is valid and eligible for public delivery.
/// </summary>
public static class PublicTimelineEligibilityResolver
{
    /// <summary>
    /// Resolves a submitted settings threshold without accepting unknown options or repairing public legacy state.
    /// </summary>
    /// <param name="wasPublic">Whether the persisted owner state was public before the submission.</param>
    /// <param name="willBePublic">Whether the submitted owner state will be public.</param>
    /// <param name="threshold">The submitted threshold option.</param>
    /// <param name="customThreshold">The submitted custom threshold value.</param>
    /// <returns>The persistence-ready threshold and validation state.</returns>
    public static PublicTimelineSettingsSubmission ResolveSettingsSubmission(
        bool wasPublic,
        bool willBePublic,
        string? threshold,
        string customThreshold)
    {
        if (!wasPublic && willBePublic && string.IsNullOrWhiteSpace(threshold))
        {
            return new PublicTimelineSettingsSubmission("1d", IsValid: true, IsCustom: false);
        }

        if (threshold == "custom")
        {
            return new PublicTimelineSettingsSubmission(customThreshold, TimespanHelper.IsValidThreshold(customThreshold), IsCustom: true);
        }

        bool isValid = (!willBePublic && string.IsNullOrWhiteSpace(threshold))
            || threshold is "now" or "1h" or "1d" or "1w" or "1m" or "1y";
        return new PublicTimelineSettingsSubmission(threshold, isValid, IsCustom: false);
    }

    /// <summary>
    /// Resolves the stored threshold without normalizing or repairing legacy values.
    /// </summary>
    /// <param name="user">The timeline owner whose persisted state is evaluated.</param>
    /// <returns>The threshold validity, delay, live state, and effective public eligibility.</returns>
    public static PublicTimelineEligibility Resolve(ApplicationUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        string? threshold = user.PublicTimelineTimeThreshold;
        if (threshold == "now")
        {
            return new PublicTimelineEligibility(
                IsThresholdValid: true,
                IsEffectivelyPublic: user.IsTimelinePublic,
                IsLive: true,
                Delay: TimeSpan.Zero);
        }

        if (threshold is not null && TimespanHelper.IsValidThreshold(threshold))
        {
            return new PublicTimelineEligibility(
                IsThresholdValid: true,
                IsEffectivelyPublic: user.IsTimelinePublic,
                IsLive: false,
                Delay: TimespanHelper.ParseTimeThreshold(threshold));
        }

        return new PublicTimelineEligibility(
            IsThresholdValid: false,
            IsEffectivelyPublic: false,
            IsLive: false,
            Delay: null);
    }
}

/// <summary>
/// Represents the public-delivery result of a persisted timeline visibility state.
/// </summary>
/// <param name="IsThresholdValid">Whether the raw persisted threshold is valid.</param>
/// <param name="IsEffectivelyPublic">Whether the timeline may be delivered publicly.</param>
/// <param name="IsLive">Whether the valid threshold is the exact live value <c>now</c>.</param>
/// <param name="Delay">The validated delay, or null when the stored threshold is invalid.</param>
public sealed record PublicTimelineEligibility(
    bool IsThresholdValid,
    bool IsEffectivelyPublic,
    bool IsLive,
    TimeSpan? Delay);

/// <summary>
/// Represents the validation result for a submitted public timeline settings threshold.
/// </summary>
/// <param name="ThresholdToPersist">The submitted threshold approved for persistence when valid.</param>
/// <param name="IsValid">Whether the submitted threshold is valid for the requested visibility state.</param>
/// <param name="IsCustom">Whether the submitted threshold uses the custom input.</param>
public sealed record PublicTimelineSettingsSubmission(string? ThresholdToPersist, bool IsValid, bool IsCustom);
