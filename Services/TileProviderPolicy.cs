using Wayfarer.Util;
using Microsoft.Extensions.Logging;

namespace Wayfarer.Services;

/// <summary>
/// Immutable, non-secret transport and retry policy captured for one upstream work series.
/// </summary>
internal sealed record TileProviderPolicy(
    string Identity,
    int SustainedRequestsPerSecond,
    int BurstCapacity,
    int MaxConcurrency,
    int MaxAttempts,
    TimeSpan FallbackBaseDelay,
    TimeSpan FallbackDelayCap,
    TimeSpan MaxIndividualWait,
    TimeSpan TotalRetryCeiling,
    bool PrefetchEnabled);

/// <summary>
/// Resolves administrator settings to an immutable provider policy without including credentials.
/// </summary>
internal static class TileProviderPolicyResolver
{
    private static readonly TileProviderPolicyDefaults Defaults = new(
        6, 20, 6, 3, 500, 4, 30, 45);

    /// <summary>Resolves the active preset or bounded custom-provider policy.</summary>
    internal static TileProviderPolicy Resolve(ApplicationSettings settings, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var canonicalOsm = TileProviderCatalog.IsCanonicalOsmTemplate(settings.TileProviderUrlTemplate);
        var preset = canonicalOsm
            ? TileProviderCatalog.FindPreset(ApplicationSettings.DefaultTileProviderKey)
            : TileProviderCatalog.FindPreset(settings.TileProviderKey);

        if (preset != null)
        {
            return Create($"builtin:{preset.Key.ToLowerInvariant()}", Defaults);
        }

        if (!settings.TileProviderAdvancedLimitsEnabled)
        {
            return Create("custom:default", Defaults);
        }

        var errors = GetCustomValidationErrors(settings);
        if (errors.Count > 0)
        {
            if (Interlocked.Exchange(ref _invalidProfileDiagnosticEmitted, 1) == 0)
            {
                logger?.LogWarning(
                    "Invalid persisted custom tile-provider limits were replaced with safe defaults.");
            }
            return Create("custom:default", Defaults);
        }

        return new TileProviderPolicy(
            "custom:advanced",
            settings.TileProviderSustainedRequestsPerSecond,
            settings.TileProviderBurstCapacity,
            settings.TileProviderMaxConcurrency,
            settings.TileProviderMaxAttempts,
            TimeSpan.FromMilliseconds(settings.TileProviderFallbackBaseDelayMs),
            TimeSpan.FromSeconds(settings.TileProviderFallbackDelayCapSeconds),
            TimeSpan.FromSeconds(settings.TileProviderMaxIndividualWaitSeconds),
            TimeSpan.FromSeconds(settings.TileProviderTotalRetryCeilingSeconds),
            PrefetchEnabled: false);
    }

    /// <summary>Validates cross-field invariants not expressible through range attributes.</summary>
    internal static IReadOnlyDictionary<string, string> ValidateCustom(ApplicationSettings settings)
    {
        var errors = GetCustomValidationErrors(settings);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors.Values));
        }

        return errors;
    }

    private static int _invalidProfileDiagnosticEmitted;

    private static Dictionary<string, string> GetCustomValidationErrors(ApplicationSettings settings)
    {
        var errors = new Dictionary<string, string>();
        AddRangeError(errors, nameof(settings.TileProviderSustainedRequestsPerSecond),
            settings.TileProviderSustainedRequestsPerSecond, 1, 20);
        AddRangeError(errors, nameof(settings.TileProviderBurstCapacity),
            settings.TileProviderBurstCapacity, 1, 50);
        AddRangeError(errors, nameof(settings.TileProviderMaxConcurrency),
            settings.TileProviderMaxConcurrency, 1, 16);
        AddRangeError(errors, nameof(settings.TileProviderMaxAttempts),
            settings.TileProviderMaxAttempts, 1, 3);
        AddRangeError(errors, nameof(settings.TileProviderFallbackBaseDelayMs),
            settings.TileProviderFallbackBaseDelayMs, 250, 5000);
        AddRangeError(errors, nameof(settings.TileProviderFallbackDelayCapSeconds),
            settings.TileProviderFallbackDelayCapSeconds, 1, 30);
        AddRangeError(errors, nameof(settings.TileProviderMaxIndividualWaitSeconds),
            settings.TileProviderMaxIndividualWaitSeconds, 1, 120);
        AddRangeError(errors, nameof(settings.TileProviderTotalRetryCeilingSeconds),
            settings.TileProviderTotalRetryCeilingSeconds, 5, 180);

        if (settings.TileProviderBurstCapacity < settings.TileProviderMaxConcurrency)
        {
            errors[nameof(settings.TileProviderBurstCapacity)] =
                "Burst capacity must be at least maximum concurrency.";
        }

        if (TimeSpan.FromSeconds(settings.TileProviderFallbackDelayCapSeconds) <
            TimeSpan.FromMilliseconds(settings.TileProviderFallbackBaseDelayMs))
        {
            errors[nameof(settings.TileProviderFallbackDelayCapSeconds)] =
                "Fallback delay cap must not be below the base delay.";
        }

        if (settings.TileProviderTotalRetryCeilingSeconds <
            settings.TileProviderMaxIndividualWaitSeconds)
        {
            errors[nameof(settings.TileProviderTotalRetryCeilingSeconds)] =
                "Total retry ceiling must not be below maximum individual wait.";
        }

        return errors;
    }

    private static void AddRangeError(
        IDictionary<string, string> errors,
        string field,
        int value,
        int minimum,
        int maximum)
    {
        if (value < minimum || value > maximum)
        {
            errors[field] = $"{field} must be between {minimum} and {maximum}.";
        }
    }

    private static TileProviderPolicy Create(string identity, TileProviderPolicyDefaults values) =>
        new(identity, values.Rate, values.Burst, values.Concurrency, values.Attempts,
            TimeSpan.FromMilliseconds(values.BaseDelayMs), TimeSpan.FromSeconds(values.DelayCapSeconds),
            TimeSpan.FromSeconds(values.MaxWaitSeconds), TimeSpan.FromSeconds(values.TotalSeconds), false);

    private sealed record TileProviderPolicyDefaults(
        int Rate, int Burst, int Concurrency, int Attempts, int BaseDelayMs,
        int DelayCapSeconds, int MaxWaitSeconds, int TotalSeconds);
}
