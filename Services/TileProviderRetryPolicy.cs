using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Wayfarer.Services;

/// <summary>
/// Holds Wayfarer's interim provider retry bounds and provider-wide not-before state.
/// These limits are conservative Wayfarer safeguards, not provider requirements.
/// </summary>
internal static class TileProviderRetryPolicy
{
    internal const int MaxAttempts = 3;
    internal static readonly TimeSpan FallbackBaseDelay = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan FallbackDelayCap = TimeSpan.FromSeconds(4);
    internal static readonly TimeSpan MaxIndividualWait = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaxInteractiveDuration = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan InvalidDelayHold = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<string, DateTimeOffset> _providerNotBefore = new();
    private static Func<DateTimeOffset> _utcNow = static () => DateTimeOffset.UtcNow;
    private static Func<int, double> _jitter = static _ => (Random.Shared.NextDouble() * 2d) - 1d;

    /// <summary>Gets the current UTC instant through the deterministic policy clock.</summary>
    internal static DateTimeOffset UtcNow => _utcNow();

    /// <summary>Returns a non-secret provider fingerprint based only on URI authority.</summary>
    internal static string GetProviderKey(string tileUrl)
    {
        var authority = new Uri(tileUrl).GetLeftPart(UriPartial.Authority).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(authority)));
    }

    /// <summary>Returns the remaining provider gate delay, removing expired state.</summary>
    internal static TimeSpan GetRemainingProviderDelay(string providerKey)
    {
        if (!_providerNotBefore.TryGetValue(providerKey, out var notBefore))
        {
            return TimeSpan.Zero;
        }

        var remaining = notBefore - UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            return remaining;
        }

        _providerNotBefore.TryRemove(
            new KeyValuePair<string, DateTimeOffset>(providerKey, notBefore));
        return TimeSpan.Zero;
    }

    /// <summary>
    /// Parses and stores provider Retry-After guidance without shortening an existing gate.
    /// Missing headers select fallback retry; unusable headers open a bounded safety gate.
    /// </summary>
    internal static ProviderDelayDecision ApplyRetryAfter(
        string providerKey,
        HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values))
        {
            return ProviderDelayDecision.Missing;
        }

        var rawValue = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(rawValue) ||
            !TryParseRetryAfter(rawValue, UtcNow, out var notBefore))
        {
            var invalidNotBefore = SafeAdd(UtcNow, InvalidDelayHold);
            ExtendProviderGate(providerKey, invalidNotBefore);
            return new ProviderDelayDecision(
                ProviderDelayKind.Invalid,
                invalidNotBefore - UtcNow);
        }

        ExtendProviderGate(providerKey, notBefore);
        return new ProviderDelayDecision(
            ProviderDelayKind.Valid,
            notBefore - UtcNow);
    }

    /// <summary>Calculates bounded exponential fallback with deterministic injectable jitter.</summary>
    internal static TimeSpan GetFallbackDelay(int failedAttempts)
    {
        var exponent = Math.Max(0, failedAttempts - 1);
        var unjitteredMilliseconds = Math.Min(
            FallbackBaseDelay.TotalMilliseconds * Math.Pow(2, exponent),
            FallbackDelayCap.TotalMilliseconds);
        var jitterSample = Math.Clamp(_jitter(failedAttempts), -1d, 1d);
        var jitteredMilliseconds = unjitteredMilliseconds * (1d + (jitterSample * 0.2d));
        return TimeSpan.FromMilliseconds(Math.Clamp(
            jitteredMilliseconds,
            1d,
            FallbackDelayCap.TotalMilliseconds));
    }

    /// <summary>Converts a remaining delay to bounded whole-second local retry guidance.</summary>
    internal static int GetBoundedRetryAfterSeconds(TimeSpan remainingDelay)
    {
        var bounded = Math.Clamp(
            remainingDelay.TotalSeconds,
            1d,
            MaxIndividualWait.TotalSeconds);
        return (int)Math.Ceiling(bounded);
    }

    /// <summary>Restores production policy state after each isolated test.</summary>
    internal static void ResetForTesting()
    {
        _providerNotBefore.Clear();
        _utcNow = static () => DateTimeOffset.UtcNow;
        _jitter = static _ => (Random.Shared.NextDouble() * 2d) - 1d;
    }

    /// <summary>Overrides clock and jitter inputs for deterministic tests.</summary>
    internal static void SetDeterminismForTesting(
        Func<DateTimeOffset>? utcNow = null,
        Func<int, double>? jitter = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _jitter = jitter ?? (_ => 0d);
    }

    /// <summary>Parses delta-seconds or an HTTP-date into a future representable instant.</summary>
    private static bool TryParseRetryAfter(
        string rawValue,
        DateTimeOffset now,
        out DateTimeOffset notBefore)
    {
        if (long.TryParse(rawValue, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            if (seconds <= 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
            {
                notBefore = default;
                return false;
            }

            return TryAdd(now, TimeSpan.FromSeconds(seconds), out notBefore);
        }

        if (!DateTimeOffset.TryParseExact(
                rawValue,
                "r",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out notBefore))
        {
            return false;
        }

        return notBefore > now;
    }

    /// <summary>Extends provider state atomically and never shortens a provider-directed delay.</summary>
    private static void ExtendProviderGate(string providerKey, DateTimeOffset candidate) =>
        _providerNotBefore.AddOrUpdate(
            providerKey,
            candidate,
            (_, current) => current >= candidate ? current : candidate);

    /// <summary>Safely adds a known bounded interval to the current instant.</summary>
    private static DateTimeOffset SafeAdd(DateTimeOffset instant, TimeSpan delay) =>
        TryAdd(instant, delay, out var result) ? result : DateTimeOffset.MaxValue;

    /// <summary>Attempts checked date arithmetic without exposing provider input.</summary>
    private static bool TryAdd(
        DateTimeOffset instant,
        TimeSpan delay,
        out DateTimeOffset result)
    {
        try
        {
            result = instant.Add(delay);
            return result > instant;
        }
        catch (ArgumentOutOfRangeException)
        {
            result = default;
            return false;
        }
    }
}

/// <summary>Classifies provider Retry-After input without retaining the raw header.</summary>
internal enum ProviderDelayKind
{
    Missing,
    Valid,
    Invalid
}

/// <summary>Describes the bounded decision produced from a provider Retry-After header.</summary>
internal readonly record struct ProviderDelayDecision(
    ProviderDelayKind Kind,
    TimeSpan Delay)
{
    internal static ProviderDelayDecision Missing =>
        new(ProviderDelayKind.Missing, TimeSpan.Zero);
}
