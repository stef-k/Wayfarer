using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NodaTime;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>Complete explicit owner publication, independent of collection readiness and predicates.</summary>
internal static class HistoryDetail
{
    // Fixed source/wire allowlists: no navigation, credentials, derived search text or future entity fields leak.
    private static readonly (string Source, string Wire)[] Stored =
    [
        ("Id", "locationId"), ("IdempotencyKey", "uuid"), ("TimeZoneId", "storedTimeZoneId"),
        ("CoordinateOrigin", "coordinateOrigin"), ("ObservedAgeAtEventMs", "observedAgeAtEventMs"),
        ("LocationType", "locationType"), ("ActivityTypeId", "activityTypeId"),
        ("Address", "address"), ("FullAddress", "fullAddress"), ("ProviderAddressLine1", "providerAddressLine1"),
        ("AddressNumber", "addressNumber"), ("StreetName", "streetName"), ("PostCode", "postCode"), ("Place", "place"),
        ("Region", "region"), ("Country", "country"), ("ResolvedFeatureName", "resolvedFeatureName"),
        ("ResolvedFeatureType", "resolvedFeatureType"), ("ReverseGeocodingProvider", "reverseGeocodingProvider"),
        ("ReverseGeocodingStorageMode", "reverseGeocodingStorageMode"), ("Source", "source"), ("IsUserInvoked", "isUserInvoked"),
        ("Provider", "provider"), ("AppVersion", "appVersion"), ("AppBuild", "appBuild"), ("DeviceModel", "deviceModel"),
        ("OsVersion", "osVersion"), ("BatteryLevel", "batteryLevel"), ("IsCharging", "isCharging")
    ];
    private static readonly (string Source, string Wire)[] Dates =
    [
        ("LocalTimestamp", "eventUtc"), ("Timestamp", "storedTimestampUtc"), ("ObservedAtUtc", "observedAtUtc"),
        ("PresenceObservedAtUtc", "presenceObservedAtUtc"), ("ReverseGeocodedAt", "reverseGeocodedAt")
    ];
    private static readonly (string Source, string Wire)[] Numbers =
        [("Accuracy", "accuracy"), ("Altitude", "altitude"), ("Speed", "speed"), ("Bearing", "bearing")];

    /// <summary>Sanitizes unrepresentable legacy values in SQL before decoding, without treating unavailable as known null.</summary>
    internal static async Task<object?> ReadAsync(ApplicationDbContext db, string owner, int id, DateTimeZone zone, CancellationToken token)
    {
        var fields = Stored.Select(field => $"l.\"{field.Source}\" AS \"{field.Wire}\"").ToList();
        fields.AddRange(Dates.Select(field => $"CASE WHEN {DateValid(field.Source)} THEN l.\"{field.Source}\" END AS \"{field.Wire}\""));
        fields.AddRange(Numbers.Select(field => $"CASE WHEN {NumberValid(field.Source)} THEN l.\"{field.Source}\" END AS \"{field.Wire}\""));
        fields.AddRange(Dates.Concat(Numbers).Select(field => $"(l.\"{field.Source}\" IS NOT NULL AND NOT ({(Dates.Contains(field) ? DateValid(field.Source) : NumberValid(field.Source))})) AS \"{field.Wire}Unavailable\""));
        fields.AddRange([
            "l.\"Notes\" AS \"notesHtml\"", "a.\"Name\" AS \"activityName\"",
            $"CASE WHEN {HistoryPredicate.PositionValid} THEN {HistoryPredicate.Longitude} END AS \"longitude\"",
            $"CASE WHEN {HistoryPredicate.PositionValid} THEN {HistoryPredicate.Latitude} END AS \"latitude\"",
            "l.\"CaptureLocalTimestamp\" AS \"captureLocal\"", "l.\"CaptureTimeZoneId\" AS \"captureZone\"",
            "l.\"CaptureOffsetSeconds\" AS \"captureOffset\"", "l.\"HistoryTimeStatus\" AS \"timeStatus\"",
            "l.\"HistoryTimeVersion\" AS \"timeVersion\"", "l.\"HistoryTimeFailureCode\" AS \"timeFailure\""
        ]);
        await using var command = ((NpgsqlConnection)db.Database.GetDbConnection()).CreateCommand();
        command.Transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = $"SELECT {string.Join(",", fields)} FROM \"Locations\" l LEFT JOIN \"ActivityTypes\" a ON a.\"Id\" = l.\"ActivityTypeId\" WHERE l.\"UserId\" = @owner AND l.\"Id\" = @id";
        command.Parameters.AddWithValue("owner", owner); command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        object? Value(string name) => reader[name] is DBNull ? null : reader[name];
        var unavailable = new Dictionary<string, string>();
        var record = new Dictionary<string, object?>();
        foreach (var field in Stored)
            record[field.Wire] = Value(field.Wire) is Guid uuid ? uuid.ToString("D") : Value(field.Wire);
        foreach (var field in Dates.Concat(Numbers))
        {
            token.ThrowIfCancellationRequested();
            if ((bool)reader[field.Wire + "Unavailable"]) { unavailable[field.Wire] = "unrepresentableStoredValue"; continue; }
            record[field.Wire] = Value(field.Wire) is DateTime instant ? HistoryQuery.Utc(instant) : Value(field.Wire);
        }
        record["notesHtml"] = RichNotes.Normalize((string?)Value("notesHtml"));
        token.ThrowIfCancellationRequested();
        record["activityName"] = Value("activityName");
        record["position"] = HistoryPublication.Position((double?)Value("longitude"), (double?)Value("latitude"));
        record["captureTime"] = HistoryPublication.Capture((DateTime?)Value("captureLocal"), (string?)Value("captureZone"),
            (int?)Value("captureOffset"), (string?)Value("timeStatus"), (string?)Value("timeVersion"), (string?)Value("timeFailure"));
        record["viewerTime"] = HistoryPublication.Viewer((DateTime?)Value("eventUtc"), zone);
        return new { subjectId = owner, profile = "owner-detail-1", observedAtUtc = HistoryQuery.Utc(DateTime.UtcNow),
            derivationVersions = HistoryPublication.Versions, record, unavailableFields = unavailable, filterMembership = "not-evaluated" };
    }

    private static string DateValid(string column) => $"isfinite(l.\"{column}\") AND l.\"{column}\" >= timestamptz '0001-01-01 00:00:00+00' AND l.\"{column}\" < timestamptz '10000-01-01 00:00:00+00'";
    private static string NumberValid(string column) => $"l.\"{column}\" > '-Infinity'::float8 AND l.\"{column}\" < 'Infinity'::float8";
}
