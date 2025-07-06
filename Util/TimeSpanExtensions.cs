namespace Wayfarer.Util;

public static class TimeSpanExtensions
{
    public static string FormatDuration(this TimeSpan? maybeDur)
    {
        var dur = maybeDur ?? TimeSpan.Zero;
        if (dur.TotalMinutes < 60)
        {
            return $"{(int)dur.TotalMinutes} min";
        }
        else
        {
            return $"{(int)dur.TotalHours} h";
        }
    }
}