using GeoTimeZone;
using NodaTime;

namespace Wayfarer.Util
{
    /// <summary>
    /// Extracts the time zone from a given coordinate.
    /// Useful when you need to know the time zone of a given location.
    /// </summary>
    public static class CoordinateTimeZoneConverter
    {
        /// <summary>
        /// Converts a local datetime at specific coordinates to UTC.
        /// If no local datetime is provided, uses the current time.
        /// </summary>
        /// <param name="latitude">The latitude of the location.</param>
        /// <param name="longitude">The longitude of the location.</param>
        /// <param name="localDateTime">The local datetime of the activity (optional).</param>
        /// <returns>The equivalent UTC datetime.</returns>
        public static DateTime ConvertToUtc(double latitude, double longitude, DateTime? localDateTime = null)
        {
            try
            {
                // Get timezone ID from coordinates
                string timeZoneId = GetTimeZoneIdFromCoordinates(latitude, longitude);

                // Load timezone using NodaTime
                IDateTimeZoneProvider tzProvider = DateTimeZoneProviders.Tzdb;
                DateTimeZone dateTimeZone = tzProvider[timeZoneId];

                // Use the provided local datetime or current time if not provided
                DateTime localDateTimeToConvert = localDateTime ?? DateTime.UtcNow;

                // Convert local datetime to LocalDateTime (NodaTime type)
                LocalDateTime localDateTimeNoda = LocalDateTime.FromDateTime(localDateTimeToConvert);

                // Convert to ZonedDateTime in the provided timezone
                ZonedDateTime zonedDateTime = localDateTimeNoda.InZoneStrictly(dateTimeZone);

                // Convert to UTC Instant and back to DateTime
                Instant utcInstant = zonedDateTime.ToInstant();
                return utcInstant.ToDateTimeUtc();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to convert coordinates and datetime to UTC", ex);
            }
        }

        /// <summary>
        /// Converts a UTC datetime to local time based on specific coordinates.
        /// </summary>
        /// <param name="latitude">The latitude of the location.</param>
        /// <param name="longitude">The longitude of the location.</param>
        /// <param name="utcDateTime">The UTC datetime to convert to local time.</param>
        /// <returns>The corresponding local datetime in the given timezone.</returns>
        public static DateTime ConvertUtcToLocal(double latitude, double longitude, DateTime utcDateTime)
        {
            try
            {
                // Coerce unspecified/local kinds into UTC so Instant.FromDateTimeUtc won’t throw
                if (utcDateTime.Kind != DateTimeKind.Utc)
                {
                    utcDateTime = DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);
                }

                // Get timezone ID from coordinates
                string timeZoneId = GetTimeZoneIdFromCoordinates(latitude, longitude);

                // Load timezone using NodaTime
                IDateTimeZoneProvider tzProvider = DateTimeZoneProviders.Tzdb;
                DateTimeZone dateTimeZone = tzProvider[timeZoneId];

                // Convert UTC datetime to Instant
                Instant instant = Instant.FromDateTimeUtc(utcDateTime);

                // Convert to local time in the specified timezone
                ZonedDateTime zonedDateTime = instant.InZone(dateTimeZone);
                LocalDateTime localDateTime = zonedDateTime.LocalDateTime;

                // Return the local datetime (without Kind)
                return localDateTime.ToDateTimeUnspecified();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to convert UTC to local datetime", ex);
            }
        }


        /// <summary>
        /// Gets the timezone ID based on the given coordinates.
        /// </summary>
        /// <param name="latitude">The latitude of the location.</param>
        /// <param name="longitude">The longitude of the location.</param>
        /// <returns>The timezone ID corresponding to the given coordinates.</returns>
        public static string GetTimeZoneIdFromCoordinates(double latitude, double longitude)
        {
            try
            {
                // Get timezone ID from coordinates using GeoTimeZone
                string timeZoneId = TimeZoneLookup.GetTimeZone(latitude, longitude).Result;
                return timeZoneId;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to get timezone ID from coordinates", ex);
            }
        }
    }
}