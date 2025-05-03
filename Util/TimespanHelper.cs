using System.Text.RegularExpressions;

namespace Wayfarer.Util;

public static class TimespanHelper
{
    /// <summary>
    /// Validates time thresholds for public timeline settings
    /// Values that should pass: 1.5d for 1 & a half days, 2m for 2 months. Valid chars: (d)ays, (m)onths, (h)ours, (y)ears
    /// minimum values: 0.1 maximum values up to 5 years, for hours that is 43800, for days 1825, for weeks 260, for months 60 and for years 5.
    /// </summary>
    /// <param name="threshold"></param>
    /// <returns>True if the threshold is valid</returns>
    public static bool IsValidThreshold(string threshold)
    {
        if (string.IsNullOrWhiteSpace(threshold)) return false;

        // Regex validation: number followed by h/d/w/m/y
        var match = Regex.Match(threshold, @"^(\d+(\.\d+)?)([hdwmy])$", RegexOptions.IgnoreCase);
        if (!match.Success) return false;

        double value = double.Parse(match.Groups[1].Value);
        string unit = match.Groups[3].Value.ToLower();

        // Enforce minimum value (0.1h) and maximum (20y)
        return unit switch
        {
            "h" => value >= 0.1 && value <= 175200, // 0.1h to ~20y in hours
            "d" => value >= (0.1 / 24) && value <= 7300, // 0.1h in days to 20y
            "w" => value >= (0.1 / 24 / 7) && value <= 1043, // 0.1h in weeks to 20y
            "m" => value >= (0.1 / 24 / 30) && value <= 240, // 0.1h in months (30d avg) to 20y
            "y" => value >= (0.1 / 24 / 365) && value <= 20, // 0.1h in years to 20y
            _ => false
        };
    }

    /// <summary>
    /// Parses time threshold up to when to show user's public timeline.
    /// </summary>
    /// <param name="threshold">Valid strings: 1h for one hour,d for days,  2.5w for 2 and a half weeks, m for months,y for years. Use now for 0 threshold.</param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public static TimeSpan ParseTimeThreshold(string threshold)
    {
        if (string.IsNullOrEmpty(threshold))
        {
            return TimeSpan.Zero; // Default to 0 if nothing selected
        }

        if (threshold.Equals("now", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.Zero; // "Now" means no time difference, so return zero time span
        }

        double timeValue = 0;
        string unit = threshold.Last().ToString().ToLower();

        switch (unit)
        {
            case "h": // Hours
                if (double.TryParse(threshold.Substring(0, threshold.Length - 1), out timeValue))
                {
                    return TimeSpan.FromHours(timeValue);
                }

                break;

            case "d": // Days
                if (double.TryParse(threshold.Substring(0, threshold.Length - 1), out timeValue))
                {
                    return TimeSpan.FromDays(timeValue);
                }

                break;

            case "w": // Weeks
                if (double.TryParse(threshold.Substring(0, threshold.Length - 1), out timeValue))
                {
                    return TimeSpan.FromDays(timeValue * 7);
                }

                break;

            case "m": // Months (approximation to 30 days)
                if (double.TryParse(threshold.Substring(0, threshold.Length - 1), out timeValue))
                {
                    return TimeSpan.FromDays(timeValue * 30);
                }

                break;

            case "y": // Years (approximation to 365 days)
                if (double.TryParse(threshold.Substring(0, threshold.Length - 1), out timeValue))
                {
                    return TimeSpan.FromDays(timeValue * 365);
                }

                break;

            default:
                throw new ArgumentException("Invalid time threshold");
        }

        throw new ArgumentException("Invalid time threshold format");
    }
}