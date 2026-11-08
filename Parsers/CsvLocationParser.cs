using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CsvHelper;
using Newtonsoft.Json.Linq;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using Wayfarer.Models;
using Wayfarer.Services.LocationProviders;
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
    public async IAsyncEnumerable<Location> ParseAsync(
        Stream fileStream,
        string userId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {

        var config = new CsvConfiguration(ParsingCulture)
        {
            BadDataFound = null,
            MissingFieldFound = null,
            HeaderValidated = null,
            PrepareHeaderForMatch = args => args.Header?.Trim() ?? string.Empty
        };

        using var reader = new StreamReader(fileStream, new UTF8Encoding(true, true), detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        using var csv = new CsvReader(reader, config);

        if (!await csv.ReadAsync() || !csv.ReadHeader())
        {
            _logger.LogWarning("CSV file had no header row.");
            yield break;
        }

        var seenZeroKey = false;
        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Native descriptors are admitted before permissive filtering, defaults or fallback.
            var raw = new JObject();
            var duplicateHeaders = false;
            foreach (var header in csv.HeaderRecord!)
            {
                var name = header.Trim();
                if (raw.ContainsKey(name)) { duplicateHeaders = true; continue; }
                raw.Add(name, GetField(csv, name));
            }
            if (raw.ContainsKey("WayfarerRecovery") && duplicateHeaders) throw LocationImportValidationException.Recovery();
            var profile = LocationRecoveryProfile.Read(raw, true);
            string? field(string name) => profile == null ? GetField(csv, name) : profile.Text(name, GetField(csv, name));
            // Validate every input identity before coordinate/timestamp filtering, independently on each pass.
            var idempotencyKey = ParseOptionalGuid(field("IdempotencyKey"));
            if (idempotencyKey == Guid.Empty)
            {
                if (seenZeroKey) throw LocationImportValidationException.MultipleZeroKeys();
                seenZeroKey = true;
            }
            // Reject invalid WGS84 input before PostGIS can normalize a geography point.
            if (!TryGetRequiredDouble(csv, "Latitude", out var latitude) ||
                !TryGetRequiredDouble(csv, "Longitude", out var longitude) ||
                !double.IsFinite(latitude) || latitude is < -90 or > 90 ||
                !double.IsFinite(longitude) || longitude is < -180 or > 180)
            {
                _logger.LogWarning("Skipping CSV record; code {Code}; row {Row}.",
                    "location-import-invalid-coordinate", csv.Context?.Parser?.Row ?? 0);
                continue;
            }

            if (!TryParseTimestampUtc(field("TimestampUtc"), out var timestampUtc))
            {
                _logger.LogWarning("Skipping CSV record due to invalid timestamp at row {Row}.",
                    csv.Context?.Parser?.Row ?? 0);
                continue;
            }
            var localTimestamp = ParseLocalTimestamp(field("LocalTimestamp"), timestampUtc);
            var timeZoneId = field("TimeZoneId");

            var activityName = field("Activity");
            var locationType = field("LocationType");
            var accuracy = GetNullableDouble(csv, "Accuracy");
            var altitude = GetNullableDouble(csv, "Altitude");
            var speed = GetNullableDouble(csv, "Speed");
            var address = field("Address");
            var fullAddress = raw.ContainsKey("FullAddress") ? field("FullAddress") : address;
            var notes = field("Notes");

            // Extract metadata fields
            var source = field("Source");
            var isUserInvoked = GetNullableBool(csv, "IsUserInvoked");
            var provider = field("Provider");
            var bearing = GetNullableDouble(csv, "Bearing");
            var appVersion = field("AppVersion");
            var appBuild = field("AppBuild");
            var deviceModel = field("DeviceModel");
            var osVersion = field("OsVersion");
            var batteryLevel = GetNullableInt(csv, "BatteryLevel");
            var isCharging = GetNullableBool(csv, "IsCharging");
            var feature = ResolvedFeatureMetadata.NormalizeImported(
                field("ResolvedFeatureName"), field("ResolvedFeatureType"),
                field("ReverseGeocodingProvider"), field("ReverseGeocodingStorageMode"),
                field("ReverseGeocodedAt"));

            var location = new Location
            {
                UserId = userId,
                Timestamp = profile?.Kind == "original" ? ObservationEvidence.Truncate(DateTime.UtcNow) : timestampUtc,
                LocalTimestamp = localTimestamp,
                TimeZoneId = string.IsNullOrWhiteSpace(timeZoneId) ? "UTC" : timeZoneId!,
                Coordinates = new GeoPoint(longitude, latitude) { SRID = 4326 },
                Accuracy = accuracy,
                Altitude = altitude,
                Speed = speed,
                Address = address,
                FullAddress = fullAddress,
                AddressNumber = field("AddressNumber"),
                StreetName = raw.ContainsKey("StreetName") ? field("StreetName") : field("Street"),
                PostCode = raw.ContainsKey("PostCode") ? field("PostCode") : field("Postcode"),
                Place = field("Place"),
                Region = field("Region"),
                Country = field("Country"),
                ProviderAddressLine1 = RetainedAddressText.Normalize(field("ProviderAddressLine1")),
                ResolvedFeatureName = feature.Name,
                ResolvedFeatureType = feature.Type,
                ReverseGeocodingProvider = feature.Provider,
                ReverseGeocodingStorageMode = feature.StorageMode,
                ReverseGeocodedAt = feature.EnrichedAt,
                Notes = notes,
                // Preserve raw text for the database-owned Activity validation and normalization.
                ImportedActivityName = string.IsNullOrEmpty(activityName) ? null : activityName,
                ActivityType = null!,
                // LocationType is independent text; only missing and zero-length values become null.
                LocationType = string.IsNullOrEmpty(locationType) ? null : locationType,

                // Metadata fields
                ImportedRecoveryProfile = profile != null,
                OriginalLocalTimestamp = ReadOriginalLocal(field("OriginalLocalTimestamp")),
                OriginalTimeZoneId = ReadOriginalZone(field("OriginalTimeZoneId")),
                OriginalUtcOffsetSeconds = ReadOriginalOffset(field("OriginalUtcOffsetSeconds")),
                SubmittedAtUtc = ReadSubmission(field("SubmittedAtUtc")),
                CoordinateOrigin = ObservationEvidence.ReadOrigin(field("CoordinateOrigin")),
                ObservedAtUtc = ObservationEvidence.ReadInstant(field("ObservedAtUtc")),
                ObservedAgeAtEventMs = ObservationEvidence.ReadAge(field("ObservedAgeAtEventMs")),
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
                IdempotencyKey = idempotencyKey
            };

            yield return location;
        }
    }

    /// <summary>New original facts reject malformed supplied values even in legacy files.</summary>
    internal static string? ReadOriginalLocal(string? value) => ObservationEvidence.ValidOriginalLocal(value)
        ? value : throw LocationImportValidationException.Recovery();
    internal static string? ReadOriginalZone(string? value) => ObservationEvidence.ValidOriginalZone(value)
        ? value : throw LocationImportValidationException.Recovery();
    internal static DateTimeOffset? ReadSubmission(string? value) => value == null ? null
        : ObservationEvidence.ReadInstant(value) ?? throw LocationImportValidationException.Recovery();
    internal static int? ReadOriginalOffset(string? value) => string.IsNullOrEmpty(value) ? null
        : int.TryParse(value, out var offset) && offset is >= -64800 and <= 64800 ? offset : throw LocationImportValidationException.Recovery();

    private static string? GetField(CsvReader csv, string field)
    {
        return csv.TryGetField(field, out string? value) ? value : null;
    }

    /// <summary>Preserves legacy optional identity and all accepted GUID spellings without demoting malformed input.</summary>
    private static Guid? ParseOptionalGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Guid.TryParse(value, out var key) ? key : throw LocationImportValidationException.InvalidKey();
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

    private static bool TryParseTimestampUtc(string? rawTimestamp, out DateTime timestampUtc)
    {
        if (ObservationEvidence.ReadInstant(rawTimestamp) is { } shared) { timestampUtc = shared.UtcDateTime; return true; }
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
        if (ObservationEvidence.ReadInstant(rawTimestamp) is { } shared) return shared.UtcDateTime;
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
