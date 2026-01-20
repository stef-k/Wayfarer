using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Wayfarer.Models;
using GeoPoint = NetTopologySuite.Geometries.Point;

namespace Wayfarer.Parsers;

/// <summary>
/// Parses Wayfarer-exported GPX files back into <see cref="Location"/> entities.
/// </summary>
public sealed class GpxLocationParser : ILocationDataParser
{
    private static readonly CultureInfo ParsingCulture = CultureInfo.InvariantCulture;
    private static readonly XNamespace WayfarerNamespace = "https://wayfarer.app/schemas/gpx";
    private readonly ILogger<GpxLocationParser> _logger;

    public GpxLocationParser(ILogger<GpxLocationParser> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<List<Location>> ParseAsync(Stream fileStream, string userId)
    {
        _logger.LogInformation("Parsing GPX data for user {UserId}.", userId);

        var document = await XDocument.LoadAsync(fileStream, LoadOptions.None, default);
        var gpxRoot = document.Root ?? throw new FormatException("GPX file does not contain a root element.");
        var gpxNamespace = gpxRoot.Name.Namespace;

        var locations = new List<Location>();

        foreach (var trkpt in gpxRoot.Descendants(gpxNamespace + "trkpt"))
        {
            var latRaw = trkpt.Attribute("lat")?.Value;
            var lonRaw = trkpt.Attribute("lon")?.Value;

            if (!TryParseDouble(latRaw, out var latitude) || !TryParseDouble(lonRaw, out var longitude))
            {
                _logger.LogWarning("Skipping GPX track point due to invalid coordinates: lat={LatRaw}, lon={LonRaw}.", latRaw, lonRaw);
                continue;
            }

            var altitude = ParseNullableDouble(trkpt.Element(gpxNamespace + "ele")?.Value);
            var timestampUtcRaw = trkpt.Element(gpxNamespace + "time")?.Value;

            var extensions = trkpt.Element(gpxNamespace + "extensions");
            var localTimestampRaw = GetExtensionValue(extensions, "localTimestamp");
            var timeZoneId = GetExtensionValue(extensions, "timeZoneId");
            var activityName = GetExtensionValue(extensions, "activity");
            var accuracy = ParseNullableDouble(GetExtensionValue(extensions, "accuracy"));
            var speed = ParseNullableDouble(GetExtensionValue(extensions, "speed"));
            var address = GetExtensionValue(extensions, "address");
            var fullAddress = GetExtensionValue(extensions, "fullAddress");
            var addressNumber = GetExtensionValue(extensions, "addressNumber");
            var streetName = GetExtensionValue(extensions, "streetName");
            var postCode = GetExtensionValue(extensions, "postCode");
            var place = GetExtensionValue(extensions, "place");
            var region = GetExtensionValue(extensions, "region");
            var country = GetExtensionValue(extensions, "country");
            var notes = GetExtensionValue(extensions, "notes");
            // Metadata fields
            var source = GetExtensionValue(extensions, "source");
            var isUserInvoked = ParseNullableBool(GetExtensionValue(extensions, "isUserInvoked"));
            var provider = GetExtensionValue(extensions, "provider");
            var bearing = ParseNullableDouble(GetExtensionValue(extensions, "bearing"));
            var appVersion = GetExtensionValue(extensions, "appVersion");
            var appBuild = GetExtensionValue(extensions, "appBuild");
            var deviceModel = GetExtensionValue(extensions, "deviceModel");
            var osVersion = GetExtensionValue(extensions, "osVersion");
            var batteryLevel = ParseNullableInt(GetExtensionValue(extensions, "batteryLevel"));
            var isCharging = ParseNullableBool(GetExtensionValue(extensions, "isCharging"));

            var timestampUtc = ParseTimestampUtc(timestampUtcRaw);
            var localTimestamp = ParseLocalTimestamp(localTimestampRaw, timestampUtc);

            var location = new Location
            {
                UserId = userId,
                Timestamp = timestampUtc,
                LocalTimestamp = localTimestamp,
                TimeZoneId = string.IsNullOrWhiteSpace(timeZoneId) ? "UTC" : timeZoneId!,
                Coordinates = new GeoPoint(longitude, latitude) { SRID = 4326 },
                Accuracy = accuracy,
                Altitude = altitude,
                Speed = speed,
                Address = address,
                FullAddress = fullAddress ?? address,
                AddressNumber = addressNumber,
                StreetName = streetName,
                PostCode = postCode,
                Place = place,
                Region = region,
                Country = country,
                Notes = notes,
                ImportedActivityName = string.IsNullOrWhiteSpace(activityName) ? null : activityName,
                ActivityType = null!,
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
                IsCharging = isCharging
            };

            locations.Add(location);
        }

        _logger.LogInformation("Parsed {Count} track points from GPX file.", locations.Count);
        return locations;
    }

    private static string? GetExtensionValue(XElement? extensionsElement, string localName)
    {
        if (extensionsElement == null)
        {
            return null;
        }

        foreach (var element in extensionsElement.Elements())
        {
            if (string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase) &&
                element.Name.Namespace == WayfarerNamespace)
            {
                return element.Value;
            }
        }

        return null;
    }

    private static bool TryParseDouble(string? raw, out double value)
    {
        return double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, ParsingCulture, out value);
    }

    private static double? ParseNullableDouble(string? raw)
    {
        return double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, ParsingCulture, out var value)
            ? value
            : null;
    }

    private static bool? ParseNullableBool(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (bool.TryParse(raw, out var b)) return b;
        if (raw == "1") return true;
        if (raw == "0") return false;
        return null;
    }

    private static int? ParseNullableInt(string? raw)
    {
        return int.TryParse(raw, NumberStyles.Integer, ParsingCulture, out var value)
            ? value
            : null;
    }

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

    private static DateTime ParseLocalTimestamp(string? rawTimestamp, DateTime fallbackUtc)
    {
        if (string.IsNullOrWhiteSpace(rawTimestamp))
        {
            return fallbackUtc;
        }

        if (DateTimeOffset.TryParse(rawTimestamp, ParsingCulture, DateTimeStyles.RoundtripKind, out var dto))
        {
            return DateTime.SpecifyKind(dto.UtcDateTime, DateTimeKind.Utc);
        }

        if (DateTime.TryParse(rawTimestamp, ParsingCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }

        return fallbackUtc;
    }
}
