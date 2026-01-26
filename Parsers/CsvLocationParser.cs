using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using Wayfarer.Models;
using GeoPoint = NetTopologySuite.Geometries.Point;

namespace Wayfarer.Parsers;

/// <summary>
/// Parses the Wayfarer CSV export format back into <see cref="Location"/> entities.
/// </summary>
public sealed class CsvLocationParser : ILocationDataParser
{
    private static readonly CultureInfo ParsingCulture = CultureInfo.InvariantCulture;
    private readonly ILogger<CsvLocationParser> _logger;

    public CsvLocationParser(ILogger<CsvLocationParser> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<List<Location>> ParseAsync(Stream fileStream, string userId)
    {
        _logger.LogDebug("Parsing CSV location data for user {UserId}.", userId);

        var config = new CsvConfiguration(ParsingCulture)
        {
            BadDataFound = null,
            MissingFieldFound = null,
            HeaderValidated = null,
            PrepareHeaderForMatch = args => args.Header?.Trim() ?? string.Empty
        };

        var locations = new List<Location>();

        using var reader = new StreamReader(fileStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        using var csv = new CsvReader(reader, config);

        if (!await csv.ReadAsync() || !csv.ReadHeader())
        {
            _logger.LogWarning("CSV file had no header row.");
            return locations;
        }

        while (await csv.ReadAsync())
        {
            if (!TryGetRequiredDouble(csv, "Latitude", out var latitude) ||
                !TryGetRequiredDouble(csv, "Longitude", out var longitude))
            {
                _logger.LogWarning("Skipping CSV record due to missing coordinates at row {Row}.", csv.Context?.Parser?.Row ?? 0);
                continue;
            }

            var timestampUtc = ParseTimestampUtc(GetField(csv, "TimestampUtc"));
            var localTimestamp = ParseLocalTimestamp(GetField(csv, "LocalTimestamp"), timestampUtc);
            var timeZoneId = GetField(csv, "TimeZoneId");

            var activityName = GetField(csv, "Activity");
            var accuracy = GetNullableDouble(csv, "Accuracy");
            var altitude = GetNullableDouble(csv, "Altitude");
            var speed = GetNullableDouble(csv, "Speed");
            var address = GetField(csv, "Address");
            var fullAddress = GetField(csv, "FullAddress") ?? address;
            var notes = GetField(csv, "Notes");

            // Extract metadata fields
            var source = GetField(csv, "Source");
            var isUserInvoked = GetNullableBool(csv, "IsUserInvoked");
            var provider = GetField(csv, "Provider");
            var bearing = GetNullableDouble(csv, "Bearing");
            var appVersion = GetField(csv, "AppVersion");
            var appBuild = GetField(csv, "AppBuild");
            var deviceModel = GetField(csv, "DeviceModel");
            var osVersion = GetField(csv, "OsVersion");
            var batteryLevel = GetNullableInt(csv, "BatteryLevel");
            var isCharging = GetNullableBool(csv, "IsCharging");

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
                FullAddress = fullAddress,
                AddressNumber = GetField(csv, "AddressNumber"),
                StreetName = GetField(csv, "StreetName"),
                PostCode = GetField(csv, "PostCode"),
                Place = GetField(csv, "Place"),
                Region = GetField(csv, "Region"),
                Country = GetField(csv, "Country"),
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

        _logger.LogInformation("Parsed {Count} location rows from CSV.", locations.Count);
        return locations;
    }

    private static string? GetField(CsvReader csv, string field)
    {
        return csv.TryGetField(field, out string? value) ? value : null;
    }

    private static double? GetNullableDouble(CsvReader csv, string field)
    {
        if (!csv.TryGetField(field, out string? raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, ParsingCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool? GetNullableBool(CsvReader csv, string field)
    {
        if (!csv.TryGetField(field, out string? raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        // Handle "0"/"1" format
        if (raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (raw == "0" || raw.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return null;
    }

    private static int? GetNullableInt(CsvReader csv, string field)
    {
        if (!csv.TryGetField(field, out string? raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return int.TryParse(raw, NumberStyles.Integer, ParsingCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool TryGetRequiredDouble(CsvReader csv, string field, out double value)
    {
        value = default;
        if (!csv.TryGetField(field, out string? raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, ParsingCulture, out value);
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
