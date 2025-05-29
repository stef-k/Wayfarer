using TimeZoneConverter;

namespace Wayfarer.Util
{
    public static class DateTimeUtils
    {
        /// <summary>
        /// Converts a UTC DateTime to local time based on the given IANA or Windows time zone ID.
        /// </summary>
        /// <param name="utcTime">UTC DateTime to convert.</param>
        /// <param name="timeZoneId">The time zone ID (IANA or Windows format, e.g., "Europe/Athens").</param>
        /// <returns>The local time in the specified time zone.</returns>
        public static DateTime ConvertUtcToLocalTime(DateTime utcTime, string timeZoneId)
        {
            if (utcTime.Kind != DateTimeKind.Utc)
                utcTime = DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);

            try
            {
                string systemTimeZoneId = TZConvert.IanaToWindows(timeZoneId); // Will do nothing if already Windows ID on Windows
                TimeZoneInfo tz = TimeZoneInfo.FindSystemTimeZoneById(systemTimeZoneId);
                return TimeZoneInfo.ConvertTimeFromUtc(utcTime, tz);
            }
            catch (Exception)
            {
                return utcTime; // Fallback to UTC if anything goes wrong
            }
        }
    }
}