using System.Globalization;
using System.Security.Claims;
using System.Text;
using CsvHelper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>Finite native value states and counterexamples through the production exporters and both parsers.</summary>
public sealed class SharedLocationRecoveryTests : TestBase
{
    /// <summary>Independent nullable text, clocks, zero and false survive both native transformations.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeRoundTripPreservesAllSharedValues(bool csv)
    {
        var source = await EffectiveAsync();
        var row = Assert.Single(await ParseAsync(source, csv));
        Assert.Null(row.Address); Assert.Equal("", row.FullAddress); Assert.Equal("  ", row.StreetName);
        Assert.Equal("\t", row.Provider); Assert.Equal("", row.AppBuild); Assert.Null(row.Source);
        Assert.Equal(0, row.BatteryLevel); Assert.False(row.IsCharging); Assert.False(row.IsUserInvoked);
        Assert.Equal(0, row.Altitude); Assert.Equal(0, row.Speed); Assert.Equal(0, row.Bearing);
        Assert.Equal("2026-11-08T12:59:59.999999950", row.OriginalLocalTimestamp);
        Assert.Equal("GMT+03:00", row.OriginalTimeZoneId); Assert.Equal(10800, row.OriginalUtcOffsetSeconds);
        Assert.Equal("2026-11-08T09:59:59.999999Z", ObservationEvidence.Format(row.SubmittedAtUtc!.Value));
        Assert.Equal("<p>EN / Αθήνα<br>second line</p>", row.Notes);
        Assert.Equal("geoapify", row.ReverseGeocodingProvider); Assert.Equal("building", row.ResolvedFeatureType);
        Assert.Equal("line", row.ProviderAddressLine1); Assert.True(row.ImportedRecoveryProfile);
        Assert.Null(row.PresenceObservedAtUtc); Assert.Null(row.OriginalCreateAddressToken);
    }

    /// <summary>Raw nanosecond instants truncate before framework parsing in each native format.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundaryAndOriginalRoleAreIndependent(bool csv)
    {
        var values = await EffectiveAsync();
        foreach (var name in LocationRecoveryProfile.InstantFields) values[name] = "2026-11-08T12:59:59.999999950+03:00";
        Describe(values);
        var row = Assert.Single(await ParseAsync(values, csv));
        Assert.Equal("2026-11-08T09:59:59.999999Z", ObservationEvidence.Format(new(row.LocalTimestamp)));
        Assert.Equal(row.LocalTimestamp, row.Timestamp); Assert.Equal(row.LocalTimestamp, row.ObservedAtUtc!.Value.UtcDateTime);
        Assert.Equal(row.LocalTimestamp, row.ReverseGeocodedAt!.Value.UtcDateTime);
        foreach (var name in new[] { "Id", "Address", "FullAddress", "AddressNumber", "StreetName", "PostCode", "Place", "Region", "Country",
            "ProviderAddressLine1", "ResolvedFeatureName", "ResolvedFeatureType", "ReverseGeocodingProvider", "ReverseGeocodingStorageMode", "ReverseGeocodedAt" }) values.Remove(name);
        values["TimeZoneId"] = "UTC";
        Describe(values, "original");
        var before = DateTime.UtcNow.AddSeconds(-1);
        var original = Assert.Single(await ParseAsync(values, csv));
        Assert.InRange(original.Timestamp, before, DateTime.UtcNow);
        Assert.Equal(row.LocalTimestamp, original.LocalTimestamp); Assert.Equal("GMT+03:00", original.OriginalTimeZoneId);
        Assert.Null(original.Address); Assert.Null(original.Source);
        // The qualified original descriptor can explicitly assert an unassigned name without a source catalog ID.
        values.Remove("ActivityTypeId"); Describe(values, "original");
        Assert.Null(Assert.Single(await ParseAsync(values, csv)).ImportedActivityName);
    }

    /// <summary>Unsupported native rows never fall back to permissive imports or claim unavailable Activity meaning.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContradictionsAndIncompleteNativeFieldsAreHeld(bool csv)
    {
        var source = await EffectiveAsync();
        foreach (var corrupt in new Action<JObject>[] {
            v => v["WayfarerRecovery"]!["version"] = 2,
            v => v["FuturePrivateBag"] = new JObject(),
            v => v.Remove("DeviceModel"),
            v => v["IsCharging"] = "false",
            v => v["OriginalTimeZoneId"] = "GMT+03:00\n",
            v => v["OriginalLocalTimestamp"] = "2026-02-30T12:00:00",
            v => v["OriginalUtcOffsetSeconds"] = 64801,
            v => v["Accuracy"] = "not-a-number",
            v => { v["Latitude"] = null; ((JArray)v["WayfarerRecovery"]!["nullFields"]!).Add("Latitude"); },
            v => v["ActivityTypeId"] = 7,
            v => ((JArray)v["WayfarerRecovery"]!["nullFields"]!).Add("Provider"),
            v => v["WayfarerRecovery"]!["unavailableFields"]!["Activity"] = "unrepresentableStoredValue"
        })
        {
            var values = (JObject)source.DeepClone(); corrupt(values);
            // A string Boolean is textually indistinguishable from a proper CSV Boolean.
            if (csv && values["IsCharging"]!.Type == JTokenType.String) continue;
            await Assert.ThrowsAsync<LocationImportValidationException>(() => ParseAsync(values, csv));
        }
    }

    /// <summary>Strict native admission rejects duplicates and Unicode replacement without changing legacy parsing.</summary>
    [Fact]
    public async Task NativeJsonCannotHideDuplicateOrMalformedText()
    {
        var values = await EffectiveAsync();
        var properties = values.ToString(Newtonsoft.Json.Formatting.None, []);
        foreach (var malformed in new[] {
            properties.Replace("\"AppBuild\":\"\"", "\"AppBuild\":\"\\ud800\""),
            properties.Replace("\"AppBuild\":\"\"", "\"AppBuild\":\"first\",\"AppBuild\":\"\"") })
        {
            var json = "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[0,0]},\"properties\":" + malformed + "}]}";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            var parser = new WayfarerGeoJsonParser(NullLogger<WayfarerGeoJsonParser>.Instance);
            await Assert.ThrowsAsync<LocationImportValidationException>(async () => { await foreach (var row in parser.ParseAsync(stream, "destination")) { } });
        }
        using var missing = new MemoryStream(Encoding.UTF8.GetBytes("{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"properties\":" + properties + "}]}"));
        var native = new WayfarerGeoJsonParser(NullLogger<WayfarerGeoJsonParser>.Instance);
        await Assert.ThrowsAsync<LocationImportValidationException>(async () => { await foreach (var row in native.ParseAsync(missing, "destination")) { } });
    }

    /// <summary>Only agreeing nonnull flat altitude permits a 3D native geometry.</summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task NativeGeometryRequiresAgreeingAltitude(double z, bool accepted)
    {
        var values = await EffectiveAsync();
        var geometry = new JObject { ["type"] = "Point", ["coordinates"] = new JArray(0, 0, z) };
        if (accepted) Assert.Single(await ParseAsync(values, false, geometry));
        else await Assert.ThrowsAsync<LocationImportValidationException>(() => ParseAsync(values, false, geometry));
        values["Altitude"] = null; Describe(values);
        await Assert.ThrowsAsync<LocationImportValidationException>(() => ParseAsync(values, false, geometry));
    }

    /// <summary>The explicit legacy exception distinguishes absent FullAddress from present null/empty.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyAddressFallbackIsPresenceAware(bool csv)
    {
        var values = await EffectiveAsync(); values.Remove("WayfarerRecovery"); values["Address"] = "Short";
        values["FullAddress"] = null;
        Assert.Equal(csv ? "" : null, Assert.Single(await ParseAsync(values, csv)).FullAddress);
        values["FullAddress"] = "";
        Assert.Equal("", Assert.Single(await ParseAsync(values, csv)).FullAddress);
        values.Remove("FullAddress");
        Assert.Equal("Short", Assert.Single(await ParseAsync(values, csv)).FullAddress);
    }

    /// <summary>Constructs real effective output rather than a second test-only schema.</summary>
    private async Task<JObject> EffectiveAsync()
    {
        var db = CreateDbContext();
        db.Locations.Add(new Location { UserId = "owner", Coordinates = new(0, 0) { SRID = 4326 }, TimeZoneId = "UTC",
            IdempotencyKey = Guid.NewGuid(), Timestamp = DateTime.UnixEpoch, LocalTimestamp = DateTime.UnixEpoch,
            Address = null, FullAddress = "", StreetName = "  ", Provider = "\t", Source = null, AppBuild = "",
            BatteryLevel = 0, IsCharging = false, IsUserInvoked = false, Altitude = 0, Speed = 0, Bearing = 0,
            OriginalLocalTimestamp = "2026-11-08T12:59:59.999999950", OriginalTimeZoneId = "GMT+03:00", OriginalUtcOffsetSeconds = 10800,
            SubmittedAtUtc = ObservationEvidence.ReadInstant("2026-11-08T12:59:59.999999950+03:00"),
            Notes = "<p>EN / Αθήνα<br>second line</p>", ProviderAddressLine1 = "line", ResolvedFeatureName = "Museum",
            ResolvedFeatureType = "building", ReverseGeocodingProvider = "geoapify", ReverseGeocodingStorageMode = "persistent",
            ReverseGeocodedAt = DateTimeOffset.UnixEpoch });
        await db.SaveChangesAsync();
        var controller = new LocationExportController(db) { ControllerContext = new() { HttpContext = new DefaultHttpContext {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test")) } } };
        var file = Assert.IsType<FileStreamResult>(await controller.GeoJson());
        using var reader = new StreamReader(file.FileStream);
        using var json = new Newtonsoft.Json.JsonTextReader(new StringReader(await reader.ReadToEndAsync())) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None };
        var properties = (JObject)JObject.Load(json)["features"]![0]!["properties"]!;
        properties["Latitude"] = 0; properties["Longitude"] = 0;
        return properties;
    }

    private static void Describe(JObject values, string kind = "effective") => values["WayfarerRecovery"] = new JObject {
        ["version"] = 1, ["kind"] = kind, ["nullFields"] = new JArray(values.Properties().Where(p => p.Value.Type == JTokenType.Null).Select(p => p.Name)),
        ["unavailableFields"] = new JObject() };

    /// <summary>Encodes the same raw native specimen through the actual streaming parsers.</summary>
    private static async Task<List<Location>> ParseAsync(JObject values, bool csv, JObject? geometry = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
        {
            if (csv)
            {
                using var output = new CsvWriter(writer, CultureInfo.InvariantCulture, true);
                foreach (var field in values.Properties()) output.WriteField(field.Name);
                await output.NextRecordAsync();
                foreach (var field in values.Properties()) output.WriteField(field.Value.Type == JTokenType.Null ? "" : field.Value is JValue v
                    ? Convert.ToString(v.Value, CultureInfo.InvariantCulture) : field.Value.ToString(Newtonsoft.Json.Formatting.None, []));
                await output.NextRecordAsync();
            }
            else await writer.WriteAsync(new JObject { ["type"] = "FeatureCollection", ["features"] = new JArray(new JObject {
                ["type"] = "Feature", ["geometry"] = geometry ?? new JObject { ["type"] = "Point", ["coordinates"] = new JArray(0, 0) }, ["properties"] = values }) }.ToString());
            await writer.FlushAsync();
        }
        stream.Position = 0;
        ILocationDataParser parser = csv ? new CsvLocationParser(NullLogger<CsvLocationParser>.Instance) : new WayfarerGeoJsonParser(NullLogger<WayfarerGeoJsonParser>.Instance);
        var result = new List<Location>();
        await foreach (var row in parser.ParseAsync(stream, "destination")) result.Add(row);
        return result;
    }
}
