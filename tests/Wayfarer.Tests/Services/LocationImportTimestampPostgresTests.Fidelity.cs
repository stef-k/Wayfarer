using System.Security.Claims;
using System.Text;
using System.Globalization;
using CsvHelper;
using Newtonsoft.Json.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

public sealed partial class LocationImportTimestampPostgresTests
{
    /// <summary>Production files, staging, reconciliation and PostgreSQL preserve independent originals and nullable content.</summary>
    [PostgresTheory]
    [InlineData(LocationImportFileType.Csv, false)]
    [InlineData(LocationImportFileType.WayfarerGeoJson, false)]
    [InlineData(LocationImportFileType.Csv, true)]
    [InlineData(LocationImportFileType.WayfarerGeoJson, true)]
    public async Task NativeWorkerRetainsRichSharedFieldsAndFirstWinner(LocationImportFileType format, bool original)
    {
        var source = await fixture.CreateUserAsync(); var destination = await fixture.CreateUserAsync();
        await using var seed = fixture.CreateContext();
        var instant = ObservationEvidence.ReadInstant("2026-11-08T12:59:59.999999950+03:00")!.Value;
        var activity = new ActivityType { Name = "Native activity " + Guid.NewGuid().ToString("N") };
        seed.ActivityTypes.Add(activity); await seed.SaveChangesAsync();
        var row = new Location { UserId = source.Id, IdempotencyKey = Guid.NewGuid(), Coordinates = new(23.73, 37.98) { SRID = 4326 },
            Timestamp = instant.UtcDateTime, LocalTimestamp = instant.UtcDateTime, TimeZoneId = "Europe/Athens", ActivityTypeId = activity.Id,
            OriginalLocalTimestamp = "2026-11-08T12:59:59.999999950", OriginalTimeZoneId = "GMT+03:00", OriginalUtcOffsetSeconds = 10800,
            SubmittedAtUtc = instant, ObservedAtUtc = instant, ObservedAgeAtEventMs = -1, CoordinateOrigin = "unknown",
            Address = null, FullAddress = "", AddressNumber = "0", StreetName = "  ", PostCode = "", Place = "Αθήνα", Region = "Αττική", Country = "Greece",
            ProviderAddressLine1 = "Line\nsecond", ResolvedFeatureName = "Museum", ResolvedFeatureType = "building",
            ReverseGeocodingProvider = "geoapify", ReverseGeocodingStorageMode = "persistent", ReverseGeocodedAt = instant,
            Notes = "<p>EN / Ελληνικά<br>Second line &quot;quote&quot;</p>", Altitude = 0, Accuracy = 0, Speed = 0, Bearing = 0,
            Source = null, IsUserInvoked = null, Provider = "", AppVersion = "", AppBuild = "0007", DeviceModel = "model", OsVersion = "os",
            BatteryLevel = 0, IsCharging = false };
        seed.Locations.Add(row); await seed.SaveChangesAsync();
        var controller = new LocationExportController(seed) { ControllerContext = new() { HttpContext = new DefaultHttpContext {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, source.Id)], "test")) } } };
        var result = Assert.IsType<FileStreamResult>(format == LocationImportFileType.Csv ? await controller.Csv() : await controller.GeoJson());
        using var reader = new StreamReader(result.FileStream, Encoding.UTF8);
        var payload = (await reader.ReadToEndAsync()).Replace("2026-11-08T09:59:59.999999Z", "2026-11-08T12:59:59.999999950+03:00");
        if (original) payload = OriginalFile(payload, format);
        var importStarted = DateTime.UtcNow.AddSeconds(-1);
        await ImportAsync(destination.Id, format, payload);
        await using var verify = fixture.CreateContext();
        var imported = await verify.Locations.AsNoTracking().SingleAsync(value => value.UserId == destination.Id);
        Assert.NotEqual(row.Id, imported.Id); Assert.Equal(row.IdempotencyKey, imported.IdempotencyKey); Assert.Equal(activity.Id, imported.ActivityTypeId);
        Assert.Equal(row.LocalTimestamp, imported.LocalTimestamp);
        if (original) Assert.InRange(imported.Timestamp, importStarted, DateTime.UtcNow);
        else Assert.Equal(row.Timestamp, imported.Timestamp);
        Assert.Equal(original ? "UTC" : row.TimeZoneId, imported.TimeZoneId);
        Assert.Equal(row.OriginalLocalTimestamp, imported.OriginalLocalTimestamp); Assert.Equal(row.OriginalTimeZoneId, imported.OriginalTimeZoneId);
        Assert.Equal(row.OriginalUtcOffsetSeconds, imported.OriginalUtcOffsetSeconds); Assert.Equal(row.SubmittedAtUtc, imported.SubmittedAtUtc);
        Assert.Equal(row.ObservedAtUtc, imported.ObservedAtUtc); Assert.Equal(-1, imported.ObservedAgeAtEventMs);
        if (original)
        {
            Assert.Null(imported.Address); Assert.Null(imported.FullAddress); Assert.Null(imported.Country);
            Assert.Null(imported.ProviderAddressLine1); Assert.Null(imported.ReverseGeocodedAt);
        }
        else
        {
        Assert.Null(imported.Address); Assert.Equal("", imported.FullAddress); Assert.Equal("  ", imported.StreetName); Assert.Equal("", imported.PostCode);
        Assert.Equal(row.AddressNumber, imported.AddressNumber); Assert.Equal(row.Place, imported.Place); Assert.Equal(row.Region, imported.Region); Assert.Equal(row.Country, imported.Country);
        Assert.Equal(row.ProviderAddressLine1, imported.ProviderAddressLine1); Assert.Equal(row.ReverseGeocodedAt, imported.ReverseGeocodedAt);
        Assert.Equal(row.ResolvedFeatureName, imported.ResolvedFeatureName); Assert.Equal(row.ResolvedFeatureType, imported.ResolvedFeatureType);
        }
        Assert.Equal(Wayfarer.Util.RichNotes.Normalize(row.Notes), imported.Notes); Assert.Null(imported.Source); Assert.Null(imported.IsUserInvoked); Assert.False(imported.IsCharging);
        Assert.Equal(0, imported.BatteryLevel); Assert.Equal(0, imported.Altitude); Assert.Equal(0, imported.Speed); Assert.Equal(0, imported.Bearing);
        Assert.Equal("0007", imported.AppBuild); Assert.Equal("model", imported.DeviceModel); Assert.Null(imported.PresenceObservedAtUtc);
        await verify.Locations.Where(value => value.Id == imported.Id).ExecuteUpdateAsync(setters => setters.SetProperty(value => value.Notes, "<p>Edited winner</p>"));
        await ImportAsync(destination.Id, format, payload);
        Assert.Equal("<p>Edited winner</p>", (await verify.Locations.AsNoTracking().SingleAsync(value => value.Id == imported.Id)).Notes);
        await Wayfarer.Services.LocationDeletionFence.DeleteAsync(verify, destination.Id, [imported.Id], null, CancellationToken.None);
        await ImportAsync(destination.Id, format, payload);
        Assert.False(await verify.Locations.AnyAsync(value => value.UserId == destination.Id));
    }

    /// <summary>A controlled original-role specimen removes later server facts and declares neutral compatibility cells.</summary>
    private static string OriginalFile(string effective, LocationImportFileType format)
    {
        JObject values;
        JObject? document = null;
        if (format == LocationImportFileType.Csv)
        {
            using var csv = new CsvReader(new StringReader(effective), CultureInfo.InvariantCulture);
            csv.Read(); csv.ReadHeader(); csv.Read();
            values = new JObject(csv.HeaderRecord!.Select(name => new JProperty(name, csv.GetField(name))));
            values["WayfarerRecovery"] = JObject.Parse(values["WayfarerRecovery"]!.Value<string>()!);
        }
        else
        {
            using var json = new Newtonsoft.Json.JsonTextReader(new StringReader(effective)) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None };
            document = JObject.Load(json); values = (JObject)document["features"]![0]!["properties"]!;
        }
        foreach (var name in new[] { "Id", "Address", "FullAddress", "AddressNumber", "StreetName", "PostCode", "Place", "Region", "Country",
            "ProviderAddressLine1", "ResolvedFeatureName", "ResolvedFeatureType", "ReverseGeocodingProvider", "ReverseGeocodingStorageMode", "ReverseGeocodedAt" }) values.Remove(name);
        var descriptor = (JObject)values["WayfarerRecovery"]!;
        descriptor["kind"] = "original";
        descriptor["nullFields"] = new JArray(((JArray)descriptor["nullFields"]!).Values<string>().Where(name => values.ContainsKey(name!)));
        values["TimestampUtc"] = values["LocalTimestamp"]!.DeepClone(); values["TimeZoneId"] = "UTC";
        if (document != null) return document.ToString(Newtonsoft.Json.Formatting.None, []);
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        using (var csv = new CsvWriter(text, CultureInfo.InvariantCulture, true))
        {
            foreach (var field in values.Properties()) csv.WriteField(field.Name);
            csv.NextRecord();
            foreach (var field in values.Properties()) csv.WriteField(field.Value is JObject obj ? obj.ToString(Newtonsoft.Json.Formatting.None, []) : field.Value.Value<string>());
            csv.NextRecord();
        }
        return text.ToString();
    }
}
