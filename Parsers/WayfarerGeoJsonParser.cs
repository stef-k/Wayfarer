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
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Wayfarer.Models;
using Wayfarer.Services.LocationProviders;
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
        /// <returns>Locations in feature order without retaining the complete collection.</returns>
        public async IAsyncEnumerable<Location> ParseAsync(
            Stream fileStream,
            string userId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var text = new StreamReader(fileStream, Encoding.UTF8, false, leaveOpen: true);
            using var json = new JsonTextReader(text)
            {
                CloseInput = false,
                MaxDepth = null,
                DateParseHandling = DateParseHandling.None
            };
            var isFeatureCollection = false;
            while (await json.ReadAsync(cancellationToken))
            {
                if (json.TokenType != JsonToken.PropertyName) continue;
                var property = (string?)json.Value;
                if (!await json.ReadAsync(cancellationToken)) yield break;
                if (string.Equals(property, "type", StringComparison.OrdinalIgnoreCase))
                {
                    isFeatureCollection = string.Equals((string?)json.Value, "FeatureCollection", StringComparison.Ordinal);
                    continue;
                }
                if (!string.Equals(property, "features", StringComparison.OrdinalIgnoreCase))
                {
                    await JsonReaderSkip.SkipValueAsync(json, cancellationToken);
                    continue;
                }
                if (!isFeatureCollection)
                    throw new FormatException("GeoJSON must be a FeatureCollection with type before features.");
                if (json.TokenType != JsonToken.StartArray)
                    throw new FormatException("GeoJSON features must be an array.");
                while (await json.ReadAsync(cancellationToken) && json.TokenType != JsonToken.EndArray)
                {
                    var featureJson = await JObject.LoadAsync(json, cancellationToken);
                    var wrapper = new JObject
                    {
                        ["type"] = "FeatureCollection",
                        ["features"] = new JArray(featureJson)
                    };
                    var feat = new GeoJsonReader()
                        .Read<FeatureCollection>(wrapper.ToString())?.FirstOrDefault();
                    // skip non‑Points
                    if (feat?.Geometry is not Point pt) continue;

                    var attrs = feat.Attributes;
                    var rawProperties = featureJson["properties"] as JObject;

                    // helper to safely get a string attribute
                    string? getString(string key)
                        => rawProperties?.GetValue(key, StringComparison.Ordinal)?.Type == JTokenType.String
                            ? rawProperties[key]!.Value<string>()
                            : attrs.Exists(key) && attrs[key] != null ? attrs[key]!.ToString() : null;

                    // Imported enrichment tuples accept only raw JSON string scalars.
                    string? getTupleString(string key)
                        => rawProperties?.GetValue(key, StringComparison.Ordinal) is { Type: JTokenType.String } token
                            ? token.Value<string>() : null;

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
                    if (!TryParseTimestampUtc(tsUtcString, out var tsUtc))
                    {
                        _logger.LogWarning("Skipping GeoJSON feature due to a missing or invalid timestamp.");
                        continue;
                    }

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
                    var feature = ResolvedFeatureMetadata.NormalizeImported(
                        getTupleString("ResolvedFeatureName"), getTupleString("ResolvedFeatureType"),
                        getTupleString("ReverseGeocodingProvider"), getTupleString("ReverseGeocodingStorageMode"),
                        getTupleString("ReverseGeocodedAt"));
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
                    var idempotencyKey = Guid.TryParse(getString("IdempotencyKey"), out var parsedKey)
                        ? parsedKey
                        : (Guid?)null;

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
                        ProviderAddressLine1 = ResolvedFeatureMetadata.NormalizeName(getTupleString("ProviderAddressLine1")),
                        ResolvedFeatureName = feature.Name,
                        ResolvedFeatureType = feature.Type,
                        ReverseGeocodingProvider = feature.Provider,
                        ReverseGeocodingStorageMode = feature.StorageMode,
                        ReverseGeocodedAt = feature.EnrichedAt,

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
                        IdempotencyKey = idempotencyKey,

                        // Activity mapping handled by LocationImportService
                        ActivityType = null!,
                        ImportedActivityName = string.IsNullOrWhiteSpace(activity) ? null : activity
                    };

                    yield return loc;
                }
                yield break;
            }
            throw new FormatException("GeoJSON does not contain a features array.");
        }

        /// <summary>
        /// Converts a timestamp string from the export into a UTC <see cref="DateTime"/>.
        /// </summary>
        /// <param name="rawTimestamp">ISO-8601 timestamp, ideally with an explicit offset.</param>
        private static bool TryParseTimestampUtc(string? rawTimestamp, out DateTime timestampUtc)
        {
            if (!string.IsNullOrWhiteSpace(rawTimestamp) &&
                DateTimeOffset.TryParse(rawTimestamp, ParsingCulture, DateTimeStyles.RoundtripKind, out var dto))
            {
                timestampUtc = dto.UtcDateTime;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(rawTimestamp) &&
                DateTime.TryParse(rawTimestamp, ParsingCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                timestampUtc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                return true;
            }

            timestampUtc = default;
            return false;
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
