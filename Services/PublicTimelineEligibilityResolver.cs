using Wayfarer.Models;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>
/// Resolves whether a persisted public timeline threshold is valid and eligible for public delivery.
/// </summary>
public static class PublicTimelineEligibilityResolver
{
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
