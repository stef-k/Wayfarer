using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Parsers
{
    /// <summary>
    /// Parser for the app's own exported GeoJSON format, using NTS.GeoJsonReader.
    /// </summary>
    public class WayfarerGeoJsonParser : ILocationDataParser
    {
        private static readonly CultureInfo ParsingCulture = CultureInfo.InvariantCulture;
        private readonly ILogger<WayfarerGeoJsonParser> _logger;

        public WayfarerGeoJsonParser(ILogger<WayfarerGeoJsonParser> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Reads a Wayfarer-generated GeoJSON stream and turns each feature into a <see cref="Location"/> row.
        /// </summary>
        /// <param name="fileStream">The uploaded GeoJSON stream.</param>
        /// <param name="userId">The user that owns the imported records.</param>
        /// <returns>All parsed <see cref="Location"/> entities ready for persistence.</returns>
        public async Task<List<Location>> ParseAsync(Stream fileStream, string userId)
        {
            _logger.LogDebug("Parsing Wayfarer-exported GeoJSON for user {UserId}.", userId);

            // 1) Read the entire JSON export
            string json;
            using (var sr = new StreamReader(fileStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
                json = await sr.ReadToEndAsync();

            // 2) Parse into a FeatureCollection (never null)
            var reader = new GeoJsonReader();
            var features = reader.Read<FeatureCollection>(json) ?? new FeatureCollection();

            var locations = new List<Location>();

            foreach (var feat in features)
            {
                // skip non‑Points
                if (feat.Geometry is not Point pt)
                    continue;

                var attrs = feat.Attributes;

                // helper to safely get a string attribute
                string? getString(string key)
                    => attrs.Exists(key) && attrs[key] != null
                        ? attrs[key]!.ToString()
                        : null;

                // helper to safely get a double? attribute
                double? getDouble(string key)
                    => attrs.Exists(key) && attrs[key] != null
                        ? Convert.ToDouble(attrs[key])
                        : (double?)null;

                // helper to safely get a bool? attribute
                bool? getBool(string key)
                    => attrs.Exists(key) && attrs[key] != null
                        ? Convert.ToBoolean(attrs[key])
                        : (bool?)null;

                // helper to safely get an int? attribute
                int? getInt(string key)
                    => attrs.Exists(key) && attrs[key] != null
                        ? Convert.ToInt32(attrs[key])
                        : (int?)null;

                // 3) Extract attributes with null guards
                var tsUtcString = getString("TimestampUtc");
                var tzId = getString("TimeZoneId") ?? "UTC";
                var tsUtc = ParseTimestampUtc(tsUtcString);

                var localTsString = getString("LocalTimestamp");
                var localTs = ParseLocalTimestamp(localTsString, tsUtc);

                var accuracy = getDouble("Accuracy");
                var altitude = getDouble("Altitude");
                var speed = getDouble("Speed");
                var activity = getString("Activity");
                var address = getString("Address");
                var fullAddress = getString("FullAddress") ?? address;
                var addressNumber = getString("AddressNumber");
                var streetName = getString("StreetName") ?? getString("Street");
                var postCode = getString("PostCode") ?? getString("Postcode");
                var place = getString("Place");
                var region = getString("Region");
                var country = getString("Country");
                var notes = getString("Notes");

                // Extract metadata fields
                var source = getString("Source");
                var isUserInvoked = getBool("IsUserInvoked");
                var provider = getString("Provider");
                var bearing = getDouble("Bearing");
                var appVersion = getString("AppVersion");
                var appBuild = getString("AppBuild");
                var deviceModel = getString("DeviceModel");
                var osVersion = getString("OsVersion");
                var batteryLevel = getInt("BatteryLevel");
                var isCharging = getBool("IsCharging");

                // 4) Construct domain object with explicit SRID
                var loc = new Location
                {
                    UserId = userId,
                    Timestamp = tsUtc,
                    LocalTimestamp = localTs,
                    TimeZoneId = tzId,
                    Coordinates = new Point(pt.X, pt.Y) { SRID = 4326 },
                    Accuracy = accuracy,
                    Altitude = altitude,
                    Speed = speed,
                    Notes = notes,
                    Address = address,
                    FullAddress = fullAddress,
                    AddressNumber = addressNumber,
                    StreetName = streetName,
                    PostCode = postCode,
                    Place = place,
                    Region = region,
                    Country = country,

                    // Metadata fields
                    Source = source,
                    IsUserInvoked = isUserInvoked,
                    Provider = provider,
                    Bearing = bearing,
                    AppVersion = appVersion,
                    AppBuild = appBuild,
                    DeviceModel = deviceModel,
                    OsVersion = osVersion,
                    BatteryLevel = batteryLevel,
                    IsCharging = isCharging,

                    // Activity mapping handled by LocationImportService
                    ActivityType = null!,
                    ImportedActivityName = string.IsNullOrWhiteSpace(activity) ? null : activity
                };

                locations.Add(loc);
            }

            _logger.LogInformation(
                "Parsed {FeatureCount} features into {LocationsCount} Location entities.",
                features.Count,
                locations.Count);

            return locations;
        }

        /// <summary>
        /// Converts a timestamp string from the export into a UTC <see cref="DateTime"/>.
        /// </summary>
        /// <param name="rawTimestamp">ISO-8601 timestamp, ideally with an explicit offset.</param>
        private static DateTime ParseTimestampUtc(string? rawTimestamp)
        {
            if (!string.IsNullOrWhiteSpace(rawTimestamp) &&
                DateTimeOffset.TryParse(rawTimestamp, ParsingCulture, DateTimeStyles.RoundtripKind, out var dto))
            {
                return dto.UtcDateTime;
            }

            if (!string.IsNullOrWhiteSpace(rawTimestamp) &&
                DateTime.TryParse(rawTimestamp, ParsingCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }

            return DateTime.UtcNow;
        }

        /// <summary>
        /// Returns the original local timestamp supplied by the export without altering its value.
        /// </summary>
        /// <param name="rawTimestamp">The timestamp string taken directly from the export.</param>
        /// <param name="fallbackUtc">Value used when no local timestamp is provided in the export.</param>
        private static DateTime ParseLocalTimestamp(string? rawTimestamp, DateTime fallbackUtc)
        {
            if (string.IsNullOrWhiteSpace(rawTimestamp))
            {
                return fallbackUtc;
            }

            if (DateTimeOffset.TryParse(rawTimestamp, ParsingCulture, DateTimeStyles.RoundtripKind, out var dtoWithOffset))
            {
                return DateTime.SpecifyKind(dtoWithOffset.DateTime, DateTimeKind.Utc);
            }

            if (DateTime.TryParse(rawTimestamp, ParsingCulture, DateTimeStyles.RoundtripKind, out var parsedLocal))
            {
                return DateTime.SpecifyKind(parsedLocal, DateTimeKind.Utc);
            }

            return fallbackUtc;
        }
    }
}
