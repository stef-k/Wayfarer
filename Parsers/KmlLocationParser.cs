using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml;
using Microsoft.Extensions.Logging;
using Wayfarer.Models;
using Wayfarer.Services.LocationProviders;
using GeoPoint = NetTopologySuite.Geometries.Point;

namespace Wayfarer.Parsers;

/// <summary>
/// Parses Wayfarer-exported KML files back into <see cref="Location"/> entities.
/// </summary>
public sealed class KmlLocationParser : ILocationDataParser
{
    private static readonly CultureInfo ParsingCulture = CultureInfo.InvariantCulture;
    private static readonly XNamespace KmlNamespace = "http://www.opengis.net/kml/2.2";
    private readonly ILogger<KmlLocationParser> _logger;

    public KmlLocationParser(ILogger<KmlLocationParser> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Location> ParseAsync(
        Stream fileStream,
        string userId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = XmlReader.Create(fileStream, new XmlReaderSettings
        {
            Async = true,
            CloseInput = false,
            DtdProcessing = DtdProcessing.Prohibit
        });
        XNamespace? namespaceToUse = null;
        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (namespaceToUse is null)
            {
                if (!string.Equals(reader.LocalName, "kml", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("KML file does not contain a kml root element.");
                namespaceToUse = string.IsNullOrEmpty(reader.NamespaceURI) ? KmlNamespace : reader.NamespaceURI;
                continue;
            }
            if (!string.Equals(reader.LocalName, "Placemark", StringComparison.Ordinal) ||
                reader.NamespaceURI != namespaceToUse.NamespaceName) continue;
            XElement placemark;
            using (var subtree = reader.ReadSubtree())
                placemark = (XElement)await XElement.LoadAsync(subtree, LoadOptions.None, cancellationToken);
            var pointElement = placemark.Element(namespaceToUse + "Point");
            if (pointElement == null)
            {
                continue;
            }

            var coordinatesRaw = pointElement.Element(namespaceToUse + "coordinates")?.Value;
            if (!TryParsePoint(coordinatesRaw, out var geometry, out var altitudeFromCoordinate))
            {
                _logger.LogWarning("Skipping KML placemark; code {Code}.",
                    "location-import-invalid-coordinate");
                continue;
            }

            var extendedData = placemark.Element(namespaceToUse + "ExtendedData");
            var timestampRaw = ReadDataValue(extendedData, namespaceToUse, "TimestampUtc") ?? placemark.Element(namespaceToUse + "name")?.Value;
            var localTimestampRaw = ReadDataValue(extendedData, namespaceToUse, "LocalTimestamp");
            var timeZoneId = ReadDataValue(extendedData, namespaceToUse, "TimeZoneId");
            var accuracy = ParseNullableDouble(ReadDataValue(extendedData, namespaceToUse, "Accuracy"));
            var speed = ParseNullableDouble(ReadDataValue(extendedData, namespaceToUse, "Speed"));
            var altitudeOverride = ParseNullableDouble(ReadDataValue(extendedData, namespaceToUse, "Altitude"));
            var address = ReadDataValue(extendedData, namespaceToUse, "Address");
            var fullAddress = ReadDataValue(extendedData, namespaceToUse, "FullAddress");
            var addressNumber = ReadDataValue(extendedData, namespaceToUse, "AddressNumber");
            var streetName = ReadDataValue(extendedData, namespaceToUse, "StreetName");
            var postCode = ReadDataValue(extendedData, namespaceToUse, "PostCode");
            var place = ReadDataValue(extendedData, namespaceToUse, "Place");
            var region = ReadDataValue(extendedData, namespaceToUse, "Region");
            var country = ReadDataValue(extendedData, namespaceToUse, "Country");
            var feature = ResolvedFeatureMetadata.NormalizeImported(
                ReadDataValue(extendedData, namespaceToUse, "ResolvedFeatureName"),
                ReadDataValue(extendedData, namespaceToUse, "ResolvedFeatureType"),
                ReadDataValue(extendedData, namespaceToUse, "ReverseGeocodingProvider"),
                ReadDataValue(extendedData, namespaceToUse, "ReverseGeocodingStorageMode"),
                ReadDataValue(extendedData, namespaceToUse, "ReverseGeocodedAt"));
            var activityName = ReadDataValue(extendedData, namespaceToUse, "Activity");
            var notes = ReadDataValue(extendedData, namespaceToUse, "Notes") ?? placemark.Element(namespaceToUse + "description")?.Value;
            // Metadata fields
            var source = ReadDataValue(extendedData, namespaceToUse, "Source");
            var isUserInvoked = ParseNullableBool(ReadDataValue(extendedData, namespaceToUse, "IsUserInvoked"));
            var provider = ReadDataValue(extendedData, namespaceToUse, "Provider");
            var bearing = ParseNullableDouble(ReadDataValue(extendedData, namespaceToUse, "Bearing"));
            var appVersion = ReadDataValue(extendedData, namespaceToUse, "AppVersion");
            var appBuild = ReadDataValue(extendedData, namespaceToUse, "AppBuild");
            var deviceModel = ReadDataValue(extendedData, namespaceToUse, "DeviceModel");
            var osVersion = ReadDataValue(extendedData, namespaceToUse, "OsVersion");
            var batteryLevel = ParseNullableInt(ReadDataValue(extendedData, namespaceToUse, "BatteryLevel"));
            var isCharging = ParseNullableBool(ReadDataValue(extendedData, namespaceToUse, "IsCharging"));

            if (!TryParseTimestampUtc(timestampRaw, out var timestampUtc))
            {
                _logger.LogWarning("Skipping KML placemark due to a missing or invalid timestamp.");
                continue;
            }
            var localTimestamp = ParseLocalTimestamp(localTimestampRaw, timestampUtc);
            var chosenAltitude = altitudeOverride ?? altitudeFromCoordinate;

            var location = new Location
            {
                UserId = userId,
                Timestamp = timestampUtc,
                LocalTimestamp = localTimestamp,
                TimeZoneId = string.IsNullOrWhiteSpace(timeZoneId) ? "UTC" : timeZoneId!,
                Coordinates = geometry,
                Accuracy = accuracy,
                Altitude = chosenAltitude,
                Speed = speed,
                Address = address,
                FullAddress = fullAddress ?? address,
                AddressNumber = addressNumber,
                StreetName = streetName,
                PostCode = postCode,
                Place = place,
                Region = region,
                Country = country,
                ProviderAddressLine1 = ResolvedFeatureMetadata.NormalizeName(ReadDataValue(extendedData, namespaceToUse, "ProviderAddressLine1")),
                ResolvedFeatureName = feature.Name,
                ResolvedFeatureType = feature.Type,
                ReverseGeocodingProvider = feature.Provider,
                ReverseGeocodingStorageMode = feature.StorageMode,
                ReverseGeocodedAt = feature.EnrichedAt,
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

            yield return location;
        }
    }

    private static string? ReadDataValue(XElement? extendedData, XNamespace ns, string name)
    {
        if (extendedData == null)
        {
            return null;
        }

        foreach (var dataElement in extendedData.Elements(ns + "Data"))
        {
            var attr = dataElement.Attribute("name");
            if (!string.Equals(attr?.Value, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return dataElement.Element(ns + "value")?.Value;
        }

        return null;
    }

    private static bool TryParsePoint(string? raw, out GeoPoint point, out double? altitude)
    {
        point = null!;
        altitude = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var firstCoordinate = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstCoordinate == null)
        {
            return false;
        }

        var parts = firstCoordinate.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        if (!double.TryParse(parts[0], NumberStyles.Float | NumberStyles.AllowThousands, ParsingCulture, out var longitude) ||
            !double.TryParse(parts[1], NumberStyles.Float | NumberStyles.AllowThousands, ParsingCulture, out var latitude))
        {
            return false;
        }

        if (parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Float | NumberStyles.AllowThousands, ParsingCulture, out var parsedAltitude))
        {
            altitude = parsedAltitude;
        }

        point = new GeoPoint(longitude, latitude) { SRID = 4326 };
        return true;
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
