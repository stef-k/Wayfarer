namespace Wayfarer.Services.LocationEnrichment;

/// <summary>Owns deterministic provider-native retry limits and safe wake calculations.</summary>
public static class LocationEnrichmentRetryPolicy
{
    /// <summary>Allows no more than three durable admissions for one authority generation.</summary>
    public static bool MayRetry(int admittedAttempts) => admittedAttempts is >= 0 and < 3;

    /// <summary>Uses the oldest admission strictly inside the rolling 24-hour window.</summary>
    public static DateTimeOffset GeoapifyWake(DateTimeOffset now, IEnumerable<DateTimeOffset> admissions)
    {
        var cutoff = now.AddHours(-24);
        var oldest = admissions.Where(item => item > cutoff).Order().FirstOrDefault();
        return oldest == default ? now.AddSeconds(5) : oldest.AddHours(24).AddSeconds(5);
    }

    /// <summary>Uses Wayfarer's UTC month meter boundary without inferring provider-account resets.</summary>
    public static DateTimeOffset MapboxWake(DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        var next = new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1).AddSeconds(5);
        return new(next);
    }

    /// <summary>Returns deterministic exponential backoff capped at four hours.</summary>
    public static TimeSpan Backoff(int admittedAttempts)
        => TimeSpan.FromMinutes(Math.Min(240, 5 * Math.Pow(2, Math.Clamp(admittedAttempts - 1, 0, 6))));
}
