using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Wayfarer.Services;

namespace Wayfarer.Parsers;

/// <summary>Finite version-1 file admission. Unsupported native evidence is held before legacy defaults or insertion.</summary>
internal sealed record LocationRecoveryProfile(string Kind, HashSet<string> NullFields)
{
    internal static readonly string[] TextFields = ["IdempotencyKey", "TimeZoneId", "Activity", "LocationType", "Notes",
        "CoordinateOrigin", "OriginalLocalTimestamp", "OriginalTimeZoneId", "Source", "Provider", "AppVersion", "AppBuild", "DeviceModel", "OsVersion",
        "Address", "FullAddress", "AddressNumber", "StreetName", "PostCode", "Place", "Region", "Country", "ProviderAddressLine1",
        "ResolvedFeatureName", "ResolvedFeatureType", "ReverseGeocodingProvider", "ReverseGeocodingStorageMode", "OriginalServer", "OriginalAccountId"];
    internal static readonly string[] InstantFields = ["TimestampUtc", "LocalTimestamp", "ObservedAtUtc", "SubmittedAtUtc", "ReverseGeocodedAt"];
    internal static readonly string[] NumberFields = ["Latitude", "Longitude", "Accuracy", "Altitude", "Speed", "Bearing"];
    internal static readonly string[] IntegerFields = ["ActivityTypeId", "ObservedAgeAtEventMs", "OriginalUtcOffsetSeconds", "BatteryLevel", "Id", "ServerId"];
    internal static readonly string[] BooleanFields = ["IsUserInvoked", "IsCharging"];
    private static readonly string[] EffectiveOnly = ["Id", "ServerId", "Address", "FullAddress", "AddressNumber", "StreetName", "PostCode", "Place", "Region", "Country",
        "ProviderAddressLine1", "ResolvedFeatureName", "ResolvedFeatureType", "ReverseGeocodingProvider", "ReverseGeocodingStorageMode", "ReverseGeocodedAt"];
    private static readonly HashSet<string> Known = [.. TextFields, .. InstantFields, .. NumberFields, .. IntegerFields, .. BooleanFields];

    /// <summary>Parses the strict descriptor and every applicable raw value before any permissive legacy parsing.</summary>
    internal static LocationRecoveryProfile? Read(JObject values, bool csv, JToken? geometry = null)
    {
        var raw = values["WayfarerRecovery"];
        ValidateAliases(values);
        if (raw == null) return null;
        try
        {
            Require(csv || geometry is JObject);
            var descriptor = csv ? JObject.Parse(raw.Value<string>()!, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }) : raw as JObject;
            Require(descriptor != null);
            Require(descriptor!.Properties().All(p => p.Name is "version" or "kind" or "nullFields" or "unavailableFields" or "sourceRecord"));
            Require(descriptor["version"]?.Type == JTokenType.Integer && descriptor["version"]!.Value<int>() == 1);
            var kind = descriptor["kind"]?.Value<string>();
            Require(kind is "original" or "effective");
            Require(descriptor["nullFields"] is JArray && descriptor["unavailableFields"] is JObject);
            var nulls = ((JArray)descriptor["nullFields"]!).Select(t => t.Type == JTokenType.String ? t.Value<string>()! : "").ToArray();
            Require(nulls.Distinct().Count() == nulls.Length && nulls.All(Known.Contains));
            var unavailable = (JObject)descriptor["unavailableFields"]!;
            foreach (var field in unavailable.Properties())
            {
                Require(Known.Contains(field.Name) && !nulls.Contains(field.Name));
                Require(field.Value.Type == JTokenType.String && field.Value.Value<string>() is "unrepresentableStoredValue" or "notRetainedOriginal");
                Require(values.ContainsKey(field.Name) && IsBlank(values[field.Name]!, csv));
            }
            var profile = new LocationRecoveryProfile(kind!, nulls.ToHashSet(StringComparer.Ordinal));
            Require(nulls.All(name => values.ContainsKey(name) && IsBlank(values[name]!, csv)));
            foreach (var field in values.Properties())
            {
                if (field.Name == "WayfarerRecovery") continue;
                Require(Known.Contains(field.Name));
                Require(kind == "effective" || !EffectiveOnly.Contains(field.Name));
                ValidateValue(field.Name, field.Value, csv, profile.NullFields, unavailable.ContainsKey(field.Name));
            }
            // Source descriptions are optional in both roles and grant no destination authority.
            // Coordinates may live in GeoJSON geometry; all remaining applicable fields are required.
            var required = Known.Except(["Id", "ServerId", "ActivityTypeId", "IdempotencyKey", "OriginalServer", "OriginalAccountId"])
                .Where(name => kind == "effective" || !EffectiveOnly.Contains(name))
                .Where(name => geometry == null || name is not ("Latitude" or "Longitude"));
            Require(required.All(values.ContainsKey));
            Require(unavailable.Count == 0); // Typed storage cannot truthfully regenerate unavailable shared input.
            Require(!profile.NullFields.Overlaps(["TimestampUtc", "LocalTimestamp", "TimeZoneId", "Latitude", "Longitude"]));
            if (kind == "effective") Require(values.ContainsKey("ActivityTypeId"));
            if (values["Activity"]?.Type == JTokenType.Null || profile.NullFields.Contains("Activity"))
                Require(!values.ContainsKey("ActivityTypeId") || IsBlank(values["ActivityTypeId"]!, csv));
            if (kind == "original")
            {
                Require(values["TimeZoneId"]?.Value<string>() == "UTC");
                Require(ObservationEvidence.ReadInstant(values["TimestampUtc"]?.Value<string>()) == ObservationEvidence.ReadInstant(values["LocalTimestamp"]?.Value<string>()));
            }
            if (descriptor["sourceRecord"] is { } source) ValidateSourceRecord(source, values, csv);
            if (geometry != null) ValidateGeometry(values, geometry, csv, profile);
            return profile;
        }
        catch (Exception error) when (error is not LocationImportValidationException && error is Newtonsoft.Json.JsonException or InvalidCastException or FormatException or OverflowException or ArgumentException)
        { throw LocationImportValidationException.Recovery(); }
    }

    /// <summary>CSV blank is known null only when explicitly named; nullable text otherwise retains empty.</summary>
    internal string? Text(string name, string? value) => NullFields.Contains(name) ? null : value;

    /// <summary>Validates raw scalar types and finite values; coercion cannot turn invalid native content into null.</summary>
    private static void ValidateValue(string name, JToken value, bool csv, HashSet<string> nulls, bool unavailable)
    {
        if (IsBlank(value, csv) && (nulls.Contains(name) || unavailable)) return;
        Require(value.Type != JTokenType.Null);
        var text = value.Type == JTokenType.String ? value.Value<string>() : Convert.ToString(((JValue)value).Value, CultureInfo.InvariantCulture);
        if (TextFields.Contains(name))
        {
            Require(value.Type == JTokenType.String);
            if (name == "OriginalLocalTimestamp") Require(ObservationEvidence.ValidOriginalLocal(text));
            if (name == "OriginalTimeZoneId") Require(ObservationEvidence.ValidOriginalZone(text));
            if (name == "CoordinateOrigin") Require(text is { Length: <= 24 });
            if (name == "IdempotencyKey" && !string.IsNullOrEmpty(text)) Require(Guid.TryParseExact(text, "D", out var id) && id.ToString("D") == text);
            if (name == "TimeZoneId") Require(!string.IsNullOrWhiteSpace(text));
            if (name == "Activity") Require(!string.IsNullOrWhiteSpace(text) && ActivityCatalog.CanTransport(text));
            return;
        }
        if (InstantFields.Contains(name)) { Require(value.Type == JTokenType.String && ObservationEvidence.ReadInstant(text) != null); return; }
        if (NumberFields.Contains(name))
        {
            Require(csv || value.Type is JTokenType.Float or JTokenType.Integer);
            Require(double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number));
            if (name == "Latitude") Require(number is >= -90 and <= 90);
            if (name == "Longitude") Require(number is >= -180 and <= 180);
            return;
        }
        if (BooleanFields.Contains(name)) { Require(csv ? bool.TryParse(text, out _) : value.Type == JTokenType.Boolean); return; }
        Require(csv || value.Type == JTokenType.Integer);
        Require(long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer));
        if (name != "ObservedAgeAtEventMs") Require(integer is >= int.MinValue and <= int.MaxValue);
        if (name == "OriginalUtcOffsetSeconds") Require(integer is >= -64800 and <= 64800);
        if (name == "BatteryLevel") Require(integer is >= 0 and <= 100);
    }

    /// <summary>Only agreeing 3D/flat altitude is admitted; optional flat coordinates cannot contradict geometry.</summary>
    private static void ValidateGeometry(JObject values, JToken geometry, bool csv, LocationRecoveryProfile profile)
    {
        Require(geometry["type"]?.Value<string>() == "Point" && geometry["coordinates"] is JArray);
        var coords = (JArray)geometry["coordinates"]!;
        Require(coords.Count is 2 or 3);
        for (var i = 0; i < coords.Count; i++)
        {
            Require(coords[i].Type is JTokenType.Integer or JTokenType.Float && double.IsFinite(coords[i].Value<double>()));
            if (i < 2)
            {
                var field = i == 0 ? "Longitude" : "Latitude";
                ValidateValue(field, coords[i], false, [], false);
                if (values.ContainsKey(field))
                    Require(values[field]!.Type is JTokenType.Integer or JTokenType.Float && values[field]!.Value<double>() == coords[i].Value<double>());
            }
        }
        if (coords.Count == 3)
            Require(!profile.NullFields.Contains("Altitude") && values["Altitude"]?.Type != JTokenType.Null
                && values["Altitude"]!.Value<double>() == coords[2].Value<double>());
    }

    /// <summary>Validates finite descriptive App lineage without granting backend root identity or deletion authority.</summary>
    private static void ValidateSourceRecord(JToken source, JObject values, bool csv)
    {
        Require(IsBlank(values["IdempotencyKey"] ?? (csv ? new JValue("") : JValue.CreateNull()), csv));
        Require(source is JObject obj && obj.Properties().Select(p => p.Name).Order().SequenceEqual(new[] { "namespace", "root" }));
        Require(source["namespace"]?.Value<string>() == "wayfarerapp-local-history-1" && source["root"] is JObject);
        var root = (JObject)source["root"]!;
        if (root.ContainsKey("localId"))
        {
            Require(root.Count == 1 && Guid.TryParseExact(root["localId"]?.Value<string>(), "D", out var id) && id != Guid.Empty
                && id.ToString("D") == root["localId"]!.Value<string>());
            return;
        }
        Require(root.Count == 2 && root["fileSha256"]?.Type == JTokenType.String
            && Regex.IsMatch(root["fileSha256"]!.Value<string>()!, @"\A[0-9a-f]{64}\z")
            && root["recordOrdinal"]?.Type == JTokenType.Integer && root["recordOrdinal"]!.Value<long>() > 0);
    }

    /// <summary>Canonical aliases cannot hide conflicting values, including known null/empty.</summary>
    private static void ValidateAliases(JObject values)
    {
        foreach (var (canonical, alias) in new[] { ("StreetName", "Street"), ("PostCode", "Postcode") })
            if (values.ContainsKey(canonical) && values.ContainsKey(alias)) Require(JToken.DeepEquals(values[canonical], values[alias]));
    }

    private static bool IsBlank(JToken token, bool csv) => csv ? token.Type == JTokenType.String && token.Value<string>() == "" : token.Type == JTokenType.Null;
    private static void Require(bool valid) { if (!valid) throw LocationImportValidationException.Recovery(); }
}
