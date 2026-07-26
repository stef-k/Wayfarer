using Microsoft.Extensions.Logging;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>Administrator-selected outbound tile traffic behavior.</summary>
public enum TileTrafficMode
{
    Interactive = 0,
    Conservative = 1,
    Custom = 2
}

/// <summary>Whether the persisted provider and endpoint may use Wayfarer's proxy/cache architecture.</summary>
public enum TileProviderCompatibility
{
    Supported = 0,
    Blocked = 1,
    InvalidOrUnsupported = 2
}

/// <summary>Non-secret compatibility result captured with an upstream work series.</summary>
internal sealed record TileProviderCompatibilityDecision(
    TileProviderCompatibility Status,
    string Source,
    string AuditSource,
    string Message);

/// <summary>
/// Immutable, non-secret compatibility, admission, transport, and retry policy captured for one work series.
/// </summary>
internal sealed record TileProviderPolicy(
    string Identity,
    TileTrafficMode TrafficMode,
    TileProviderCompatibilityDecision Compatibility,
    bool UsesRateTokens,
    int SustainedRequestsPerSecond,
    int BurstCapacity,
    int MaxConcurrency,
    int ClientSeriesPerMinute,
    int MaxAttempts,
    TimeSpan FallbackBaseDelay,
    TimeSpan FallbackDelayCap,
    TimeSpan MaxIndividualWait,
    TimeSpan TotalRetryCeiling,
    bool PrefetchEnabled)
{
    /// <summary>True only when the complete captured compatibility and scalar policy is eligible.</summary>
    internal bool CanContactProvider => Compatibility.Status == TileProviderCompatibility.Supported;

    /// <summary>Whether sustained-rate admission is effective for this policy.</summary>
    internal bool IsRateActive => CanContactProvider && UsesRateTokens;

    /// <summary>Whether burst-token admission is effective for this policy.</summary>
    internal bool IsBurstActive => CanContactProvider && UsesRateTokens;

    /// <summary>Whether provider-contact concurrency is effective for this policy.</summary>
    internal bool IsConcurrencyActive => CanContactProvider;

    /// <summary>Whether the per-client admitted-series allowance is effective.</summary>
    internal bool IsClientSeriesAllowanceActive => CanContactProvider && ClientSeriesPerMinute > 0;
}

/// <summary>Resolves persisted settings into one deterministic, fail-closed work-series policy.</summary>
internal static class TileProviderPolicyResolver
{
    private const string WayfarerSafeguardsSource = "Wayfarer safeguards";
    /// <summary>Resolves compatibility before selecting an active traffic mode.</summary>
    internal static TileProviderPolicy Resolve(ApplicationSettings settings, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var compatibility = TileProviderCatalog.ResolveCompatibility(
            settings.TileProviderKey, settings.TileProviderUrlTemplate);
        if (compatibility.Status != TileProviderCompatibility.Supported)
        {
            return Disabled(settings, compatibility);
        }

        var preset = TileProviderCatalog.FindPreset(settings.TileProviderKey);
        var mode = preset == null && settings.TileProviderAdvancedLimitsEnabled
            ? TileTrafficMode.Custom
            : settings.TileTrafficMode == TileTrafficMode.Conservative
                ? TileTrafficMode.Conservative
                : TileTrafficMode.Interactive;

        if (mode == TileTrafficMode.Custom)
        {
            var errors = GetCustomValidationErrors(settings);
            if (errors.Count > 0)
            {
                logger?.LogWarning("Invalid persisted custom tile-provider limits prevent upstream traffic.");
                return Disabled(settings, new TileProviderCompatibilityDecision(
                    TileProviderCompatibility.InvalidOrUnsupported,
                    WayfarerSafeguardsSource,
                    WayfarerSafeguardsSource,
                    "Custom traffic values are invalid and must be corrected before activation."));
            }

            return Create(settings, mode, compatibility, true,
                settings.TileProviderSustainedRequestsPerSecond,
                settings.TileProviderBurstCapacity,
                settings.TileProviderMaxConcurrency,
                settings.TileOutboundBudgetPerIpPerMinute);
        }

        if (mode == TileTrafficMode.Conservative)
        {
            return Create(settings, mode, compatibility, true, 12, 40, 8, 480);
        }

        // Interactive deliberately has no proactive rate, burst, global-token, or client-series admission.
        return Create(
            settings,
            mode,
            compatibility,
            false,
            0,
            0,
            TileWorkScheduler.ForegroundConcurrency,
            0);
    }

    /// <summary>Validates all Custom scalars and cross-field invariants.</summary>
    internal static IReadOnlyDictionary<string, string> ValidateCustom(ApplicationSettings settings)
    {
        var errors = GetCustomValidationErrors(settings);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors.Values));
        }

        return errors;
    }

    private static TileProviderPolicy Create(
        ApplicationSettings settings,
        TileTrafficMode mode,
        TileProviderCompatibilityDecision compatibility,
        bool usesRateTokens,
        int rate,
        int burst,
        int concurrency,
        int allowance) =>
        new($"{settings.TileProviderKey?.Trim().ToLowerInvariant()}:{mode.ToString().ToLowerInvariant()}",
            mode, compatibility, usesRateTokens, rate, burst, concurrency, allowance,
            settings.TileProviderMaxAttempts,
            TimeSpan.FromMilliseconds(settings.TileProviderFallbackBaseDelayMs),
            TimeSpan.FromSeconds(settings.TileProviderFallbackDelayCapSeconds),
            TimeSpan.FromSeconds(settings.TileProviderMaxIndividualWaitSeconds),
            TimeSpan.FromSeconds(settings.TileProviderTotalRetryCeilingSeconds), false);

    private static TileProviderPolicy Disabled(
        ApplicationSettings settings,
        TileProviderCompatibilityDecision compatibility) =>
        Create(settings, settings.TileTrafficMode, compatibility, false, 0, 0, 1, 0);

    private static Dictionary<string, string> GetCustomValidationErrors(ApplicationSettings settings)
    {
        var errors = new Dictionary<string, string>();
        AddRangeError(errors, nameof(settings.TileProviderSustainedRequestsPerSecond), settings.TileProviderSustainedRequestsPerSecond, 1, 20);
        AddRangeError(errors, nameof(settings.TileProviderBurstCapacity), settings.TileProviderBurstCapacity, 1, 50);
        AddRangeError(errors, nameof(settings.TileProviderMaxConcurrency), settings.TileProviderMaxConcurrency, 1, 16);
        AddRangeError(errors, nameof(settings.TileOutboundBudgetPerIpPerMinute), settings.TileOutboundBudgetPerIpPerMinute, 0, 1000);
        AddRangeError(errors, nameof(settings.TileProviderMaxAttempts), settings.TileProviderMaxAttempts, 1, 3);
        AddRangeError(errors, nameof(settings.TileProviderFallbackBaseDelayMs), settings.TileProviderFallbackBaseDelayMs, 250, 5000);
        AddRangeError(errors, nameof(settings.TileProviderFallbackDelayCapSeconds), settings.TileProviderFallbackDelayCapSeconds, 1, 30);
        AddRangeError(errors, nameof(settings.TileProviderMaxIndividualWaitSeconds), settings.TileProviderMaxIndividualWaitSeconds, 1, 120);
        AddRangeError(errors, nameof(settings.TileProviderTotalRetryCeilingSeconds), settings.TileProviderTotalRetryCeilingSeconds, 5, 180);

        if (settings.TileProviderBurstCapacity < settings.TileProviderMaxConcurrency)
            errors[nameof(settings.TileProviderBurstCapacity)] = "Burst capacity must be at least maximum concurrency.";
        if (TimeSpan.FromSeconds(settings.TileProviderFallbackDelayCapSeconds) < TimeSpan.FromMilliseconds(settings.TileProviderFallbackBaseDelayMs))
            errors[nameof(settings.TileProviderFallbackDelayCapSeconds)] = "Fallback delay cap must not be below the base delay.";
        if (settings.TileProviderTotalRetryCeilingSeconds < settings.TileProviderMaxIndividualWaitSeconds)
            errors[nameof(settings.TileProviderTotalRetryCeilingSeconds)] = "Total retry ceiling must not be below maximum individual wait.";

        return errors;
    }

    private static void AddRangeError(IDictionary<string, string> errors, string field, int value, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
            errors[field] = $"{field} must be between {minimum} and {maximum}.";
    }
}
