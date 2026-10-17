using System.Globalization;

namespace Wayfarer.Util;

/// <summary>Formats saved durations for Trip presentation without inventing unavailable estimates.</summary>
public static class TimeSpanExtensions
{
    /// <summary>Returns the existing coarse minute/hour label, or null when no duration is saved.</summary>
    public static string? FormatDuration(this TimeSpan? maybeDur)
    {
        if (maybeDur is not { } dur) return null;
        if (dur.TotalMinutes < 60)
        {
            return $"{(int)dur.TotalMinutes} min";
        }
        else
        {
            return $"{(int)dur.TotalHours} h";
        }
    }

    /// <summary>Preserves total hours across day boundaries using the existing hours:minutes presentation.</summary>
    public static string? FormatHoursMinutes(this TimeSpan? duration) => duration is { } value
        ? string.Create(CultureInfo.InvariantCulture, $"{value.Ticks / TimeSpan.TicksPerHour:D2}:{value.Minutes:D2}") : null;
}
