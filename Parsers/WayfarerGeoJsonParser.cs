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
        private readonly ILogger<WayfarerGeoJsonParser> _logger;

        public WayfarerGeoJsonParser(ILogger<WayfarerGeoJsonParser> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<List<Location>> ParseAsync(Stream fileStream, string userId)
        {
            _logger.LogInformation("Parsing Wayfarer-exported GeoJSON for user {UserId}.", userId);

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

                // helper to safely parse a UTC DateTime
                DateTime parseUtc(string input)
                {
                    var dt = DateTime.Parse(input, null, DateTimeStyles.RoundtripKind);
                    return dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                }

                // 3) Extract attributes with null guards
                var tsUtcString = getString("TimestampUtc");
                var tsUtc = tsUtcString != null
                    ? parseUtc(tsUtcString)
                    : DateTime.UtcNow;

                var localTsString = getString("LocalTimestamp");
                DateTime? localTs = localTsString != null
                    ? parseUtc(localTsString)
                    : null;

                var tzId = getString("TimeZoneId") ?? "UTC";
                var accuracy = getDouble("Accuracy");
                var altitude = getDouble("Altitude");
                var speed = getDouble("Speed");
                var activity = getString("Activity");
                var address = getString("Address");
                var notes = getString("Notes");

                // 4) Construct domain object
                var loc = new Location
                {
                    UserId = userId,
                    Timestamp = tsUtc,
                    LocalTimestamp = localTs ?? tsUtc,
                    TimeZoneId = tzId,
                    Coordinates = pt,
                    Accuracy = accuracy,
                    Altitude = altitude,
                    Speed = speed,
                    Notes = notes,
                    FullAddress = address,

                    // TODO: map 'activity' to your ActivityType lookup
                    ActivityType = /* e.g. ResolveActivity(activity) */ null!
                };

                locations.Add(loc);
            }

            _logger.LogInformation(
                "Parsed {FeatureCount} features into {LocationsCount} Location entities.",
                features.Count,
                locations.Count);

            return locations;
        }
    }
}
