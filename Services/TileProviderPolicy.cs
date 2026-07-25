using Wayfarer.Util;

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
    internal static TileProviderPolicy Resolve(ApplicationSettings settings)
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

        ValidateCustom(settings);
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
        var errors = new Dictionary<string, string>();
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

        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors.Values));
        }

        return errors;
    }

    private static TileProviderPolicy Create(string identity, TileProviderPolicyDefaults values) =>
        new(identity, values.Rate, values.Burst, values.Concurrency, values.Attempts,
            TimeSpan.FromMilliseconds(values.BaseDelayMs), TimeSpan.FromSeconds(values.DelayCapSeconds),
            TimeSpan.FromSeconds(values.MaxWaitSeconds), TimeSpan.FromSeconds(values.TotalSeconds), false);

    private sealed record TileProviderPolicyDefaults(
        int Rate, int Burst, int Concurrency, int Attempts, int BaseDelayMs,
        int DelayCapSeconds, int MaxWaitSeconds, int TotalSeconds);
}
