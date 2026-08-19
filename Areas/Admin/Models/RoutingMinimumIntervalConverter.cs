namespace Wayfarer.Areas.Admin.Models;

/// <summary>Converts strict ASCII decimal seconds to the persisted integer millisecond contract.</summary>
public static class RoutingMinimumIntervalConverter
{
    /// <summary>Parses zero through sixty seconds with at most one fractional digit.</summary>
    public static bool TryParse(string? rawValue, out int milliseconds)
    {
        milliseconds = 0;
        if (rawValue == null) return false;
        var value = rawValue.Trim();
        if (value.Length == 0 || value.Any(character => character is not (>= '0' and <= '9') and not '.')) return false;
        var parts = value.Split('.');
        if (parts.Length > 2 || parts[0].Length == 0 || parts.Length == 2 && parts[1].Length != 1) return false;
        if (!TryAsciiInteger(parts[0], out var whole) || whole > 60) return false;
        var tenths = parts.Length == 2 ? parts[1][0] - '0' : 0;
        if (whole == 60 && tenths != 0) return false;
        try { milliseconds = checked(checked(whole * 10 + tenths) * 100); }
        catch (OverflowException) { return false; }
        return true;
    }

    /// <summary>Formats persisted milliseconds as decimal seconds with exactly one digit.</summary>
    public static string Format(int milliseconds)
    {
        if (milliseconds is < 0 or > 60000 || milliseconds % 100 != 0)
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        return $"{milliseconds / 1000}.{milliseconds % 1000 / 100}";
    }

    private static bool TryAsciiInteger(string value, out int result)
    {
        result = 0;
        foreach (var character in value)
        {
            try { result = checked(result * 10 + character - '0'); }
            catch (OverflowException) { return false; }
        }
        return true;
    }
}
