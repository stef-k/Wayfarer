using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace Wayfarer.Services.LocationMutations;

/// <summary>Signed replaceable execution authority; never contains or replaces a client's observed originals.</summary>
internal sealed record LocationMutationBase(string Contract, string Normalization, string Owner, int LocationId,
    Guid? Uuid, Guid BaseId, DateTime IssuedAtUtc, DateTime ExpiresAtUtc)
{
    private const string Purpose = "Wayfarer.LocationMutation.location-mutation-1";

    /// <summary>Protects an immutable scope using the application's existing key custody.</summary>
    internal string Protect(IDataProtectionProvider protection) => protection.CreateProtector(Purpose).Protect(JsonSerializer.Serialize(this));

    /// <summary>Authenticates even expired authority for read-only future exclusion.</summary>
    internal static LocationMutationBase? Read(IDataProtectionProvider protection, string? handle, string owner, int id, Guid? baseId = null)
    {
        try
        {
            if (string.IsNullOrEmpty(handle)) return null;
            var value = JsonSerializer.Deserialize<LocationMutationBase>(protection.CreateProtector(Purpose).Unprotect(handle));
            return value is { Contract: "location-mutation-1" } && value.Owner == owner && value.LocationId == id
                && (baseId == null || value.BaseId == baseId) ? value : null;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException) { return null; }
    }
}

/// <summary>Same-snapshot publication of ordinary originals and raw damaged-record delete proof.</summary>
internal static class LocationMutationEvidence
{
    // This ordered inventory is the immutable location-delete-storage-1 storage encoding.
    private static readonly (string Name, string Type)[] RawColumns =
    [
        ("UserId", "text"), ("Id", "int4"), ("IdempotencyKey", "uuid"), ("Coordinates", "geometry"),
        ("LocalTimestamp", "timestamptz"), ("Timestamp", "timestamptz"), ("Notes", "text"), ("ActivityTypeId", "int4"), ("TimeZoneId", "text"),
        ("Address", "text"), ("FullAddress", "text"), ("AddressNumber", "text"), ("StreetName", "text"), ("PostCode", "text"),
        ("Place", "text"), ("Region", "text"), ("Country", "text"), ("ProviderAddressLine1", "text"), ("ResolvedFeatureName", "text"),
        ("ResolvedFeatureType", "text"), ("ReverseGeocodingProvider", "text"), ("ReverseGeocodingStorageMode", "text"), ("ReverseGeocodedAt", "timestamptz"),
        ("Accuracy", "float8"), ("Altitude", "float8"), ("Speed", "float8"), ("Bearing", "float8"), ("LocationType", "text"),
        ("Source", "text"), ("Provider", "text"), ("AppVersion", "text"), ("AppBuild", "text"), ("DeviceModel", "text"),
        ("OsVersion", "text"), ("IsUserInvoked", "bool"), ("IsCharging", "bool"), ("BatteryLevel", "int4")
    ];

    /// <summary>Raw SQL projections avoid NTS/DateTime decoding, normalization or repair of deletion consent.</summary>
    internal static IEnumerable<string> RawProjection() => RawColumns.Select((field, index) =>
    {
        var column = $"l.\"{field.Name}\"";
        var expression = field.Type switch
        {
            "text" => $"convert_to({column}, 'UTF8')",
            "geometry" => $"ST_AsEWKB({column}::geometry, 'XDR')",
            "uuid" or "timestamptz" => $"{field.Type}_send({column})",
            _ => $"{field.Type}send({column})"
        };
        return $"{expression} AS \"mutationRaw{index}\"";
    });

    /// <summary>Hashes PostgreSQL's logical binary values with explicit null tags and field frames.</summary>
    internal static string DeleteFingerprint(NpgsqlDataReader reader)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        LocationMutationCommand.Frame(hash, Encoding.UTF8.GetBytes("location-delete-storage-1"));
        for (var index = 0; index < RawColumns.Length; index++)
        {
            LocationMutationCommand.Frame(hash, Encoding.UTF8.GetBytes(RawColumns[index].Name));
            var value = reader[$"mutationRaw{index}"];
            hash.AppendData(value is DBNull ? [0] : [1]);
            LocationMutationCommand.Frame(hash, value is DBNull ? [] : (byte[])value);
        }
        return "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Issues authority and eight explicit slots from the already read owner snapshot.</summary>
    internal static object Create(IDataProtectionProvider protection, string owner, int id,
        Dictionary<string, object?> record, Dictionary<string, string> unavailable, string fingerprint, bool pending, DateTime now)
    {
        now = new DateTime(now.Ticks / 10 * 10, DateTimeKind.Utc);
        var uuid = record["uuid"] is string key ? Guid.Parse(key) : (Guid?)null;
        var authority = new LocationMutationBase("location-mutation-1", "location-values-1", owner, id, uuid, Guid.NewGuid(), now, now.AddMinutes(15));
        object Slot(string name) => unavailable.ContainsKey(name)
            ? new { state = "unavailable", reason = "unrepresentableStoredValue" } : Known(record.GetValueOrDefault(name));
        var addressUnavailable = LocationMutationCommand.AddressMembers.Any(unavailable.ContainsKey);
        var position = JsonSerializer.SerializeToElement(record["position"]);
        var positionKnown = position.TryGetProperty("latitude", out var latitude) && position.TryGetProperty("longitude", out _);
        var values = new Dictionary<string, object?>
        {
            ["uuid"] = Slot("uuid"), ["eventUtc"] = Slot("eventUtc"), ["notesHtml"] = Slot("notesHtml"),
            ["activityTypeId"] = Slot("activityTypeId"), ["altitude"] = Slot("altitude"),
            ["position"] = !positionKnown ? new { state = "unavailable", reason = "unrepresentableStoredValue" }
                : Known(new { latitude = latitude.GetDouble(), longitude = position.GetProperty("longitude").GetDouble() }),
            ["address"] = addressUnavailable ? new { state = "unavailable", reason = "unrepresentableStoredValue" }
                : Known(LocationMutationCommand.AddressMembers.ToDictionary(name => name, name => record.GetValueOrDefault(name))),
            ["deleteContent"] = Known(new { format = "location-delete-storage-1", fingerprint })
        };
        return new { contract = authority.Contract, normalization = authority.Normalization, baseId = authority.BaseId,
            baseHandle = authority.Protect(protection), issuedAtUtc = authority.IssuedAtUtc, expiresAtUtc = authority.ExpiresAtUtc,
            originalCreateAddressPublication = pending ? "pending" : "closed", baseValues = values };
    }

    /// <summary>Known null remains a value rather than an unavailable or omitted original.</summary>
    private static object Known(object? value) => new { state = "known", value };
}
