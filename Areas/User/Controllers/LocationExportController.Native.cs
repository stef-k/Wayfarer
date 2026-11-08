using System.Globalization;
using System.Text;
using CsvHelper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Util;

namespace Wayfarer.Areas.User.Controllers;

/// <summary>Canonical finite CSV and 2D GeoJSON projections share one explicit retained-field inventory.</summary>
public partial class LocationExportController
{
    /// <summary>Emits all effective fields with independently described null and unavailable evidence.</summary>
    [HttpGet]
    public async Task<IActionResult> GeoJson()
    {
        var features = new JArray();
        foreach (var location in await UserLocations().ToListAsync())
        {
            var properties = NativeProperties(location);
            properties.Remove("Latitude");
            properties.Remove("Longitude");
            features.Add(new JObject
            {
                ["type"] = "Feature",
                ["geometry"] = new JObject { ["type"] = "Point", ["coordinates"] = new JArray(location.Coordinates.X, location.Coordinates.Y) },
                ["properties"] = properties
            });
        }
        return File(new MemoryStream(Encoding.UTF8.GetBytes(new JObject { ["type"] = "FeatureCollection", ["features"] = features }.ToString(Formatting.None, []))),
            "application/geo+json", $"{GenerateTitle()}.geojson");
    }

    /// <summary>Quotes the same descriptor into a CSV cell; blank cells have explicit null evidence.</summary>
    [HttpGet]
    public async Task<IActionResult> Csv()
    {
        var rows = (await UserLocations().ToListAsync()).Select(NativeProperties).ToArray();
        var columns = NativeProperties(new Location { UserId = "", TimeZoneId = "UTC",
            Coordinates = new(0, 0), Timestamp = DateTime.UnixEpoch, LocalTimestamp = DateTime.UnixEpoch }).Properties().Select(p => p.Name).ToArray();
        var stream = new MemoryStream();
        await using (var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true))
        using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            foreach (var column in columns) csv.WriteField(column);
            await csv.NextRecordAsync();
            foreach (var row in rows)
            {
                foreach (var column in columns)
                {
                    var token = row[column]!;
                    csv.WriteField(token.Type == JTokenType.Null ? null : token is JValue scalar
                        ? Convert.ToString(scalar.Value, CultureInfo.InvariantCulture) : token.ToString(Formatting.None, []));
                }
                await csv.NextRecordAsync();
            }
            await writer.FlushAsync();
        }
        stream.Position = 0;
        return File(stream, "text/csv", $"{GenerateTitle()}.csv");
    }

    /// <summary>Describes effective history, never reconstructs a captured original or synthesizes missing addresses.</summary>
    private static JObject NativeProperties(Location l)
    {
        var values = new Dictionary<string, object?>
        {
            ["Id"] = l.Id, ["IdempotencyKey"] = l.IdempotencyKey?.ToString("D"),
            ["Latitude"] = l.Coordinates.Y, ["Longitude"] = l.Coordinates.X,
            ["TimestampUtc"] = Instant(l.Timestamp), ["LocalTimestamp"] = Instant(l.LocalTimestamp), ["TimeZoneId"] = l.TimeZoneId,
            ["OriginalLocalTimestamp"] = l.OriginalLocalTimestamp, ["OriginalTimeZoneId"] = l.OriginalTimeZoneId,
            ["OriginalUtcOffsetSeconds"] = l.OriginalUtcOffsetSeconds, ["SubmittedAtUtc"] = Instant(l.SubmittedAtUtc),
            ["Accuracy"] = l.Accuracy, ["Altitude"] = l.Altitude, ["Speed"] = l.Speed, ["Bearing"] = l.Bearing,
            ["ActivityTypeId"] = l.ActivityTypeId, ["Activity"] = l.ActivityType?.Name, ["LocationType"] = string.IsNullOrEmpty(l.LocationType) ? null : l.LocationType,
            ["Notes"] = RichNotes.Normalize(l.Notes), ["Address"] = l.Address, ["FullAddress"] = l.FullAddress,
            ["AddressNumber"] = l.AddressNumber, ["StreetName"] = l.StreetName, ["PostCode"] = l.PostCode, ["Place"] = l.Place,
            ["Region"] = l.Region, ["Country"] = l.Country, ["ProviderAddressLine1"] = l.ProviderAddressLine1,
            ["ResolvedFeatureName"] = l.ResolvedFeatureName, ["ResolvedFeatureType"] = l.ResolvedFeatureType,
            ["ReverseGeocodingProvider"] = l.ReverseGeocodingProvider, ["ReverseGeocodingStorageMode"] = l.ReverseGeocodingStorageMode,
            ["ReverseGeocodedAt"] = Instant(l.ReverseGeocodedAt), ["CoordinateOrigin"] = l.CoordinateOrigin,
            ["ObservedAtUtc"] = Instant(l.ObservedAtUtc), ["ObservedAgeAtEventMs"] = l.ObservedAgeAtEventMs,
            ["Source"] = l.Source, ["IsUserInvoked"] = l.IsUserInvoked, ["Provider"] = l.Provider,
            ["AppVersion"] = l.AppVersion, ["AppBuild"] = l.AppBuild, ["DeviceModel"] = l.DeviceModel,
            ["OsVersion"] = l.OsVersion, ["BatteryLevel"] = l.BatteryLevel, ["IsCharging"] = l.IsCharging
        };
        var unavailable = new JObject();
        foreach (var field in LocationRecoveryProfile.NumberFields)
            if (values[field] is double number && !double.IsFinite(number)) Mark(field);
        if (string.IsNullOrWhiteSpace(l.TimeZoneId)) Mark("TimeZoneId");
        if (l.ActivityTypeId != null && string.IsNullOrWhiteSpace(l.ActivityType?.Name)) Mark("Activity");
        if (!ObservationEvidence.ValidOriginalLocal(l.OriginalLocalTimestamp)) Mark("OriginalLocalTimestamp");
        if (!ObservationEvidence.ValidOriginalZone(l.OriginalTimeZoneId)) Mark("OriginalTimeZoneId");
        if (l.OriginalUtcOffsetSeconds is < -64800 or > 64800) Mark("OriginalUtcOffsetSeconds");
        if (l.BatteryLevel is < 0 or > 100) Mark("BatteryLevel");
        foreach (var (name, raw) in new[] { ("TimestampUtc", (DateTimeOffset?)new DateTimeOffset(l.Timestamp)),
            ("LocalTimestamp", new DateTimeOffset(l.LocalTimestamp)), ("ObservedAtUtc", l.ObservedAtUtc),
            ("SubmittedAtUtc", l.SubmittedAtUtc), ("ReverseGeocodedAt", l.ReverseGeocodedAt) })
            if (raw is { } date && (date == DateTimeOffset.MinValue || date == DateTimeOffset.MaxValue)) Mark(name);
        var result = JObject.FromObject(values);
        result["WayfarerRecovery"] = new JObject
        {
            ["version"] = 1, ["kind"] = "effective", ["nullFields"] = new JArray(values.Where(p => p.Value == null && !unavailable.ContainsKey(p.Key)).Select(p => p.Key)),
            ["unavailableFields"] = unavailable
        };
        return result;

        void Mark(string field) { values[field] = null; unavailable[field] = "unrepresentableStoredValue"; }
    }

    private static string Instant(DateTime value) => ObservationEvidence.Format(new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
    private static string? Instant(DateTimeOffset? value) => value is { } instant ? ObservationEvidence.Format(instant) : null;
}
