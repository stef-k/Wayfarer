using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Services.LocationImports;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class LocationCoordinatesPostgresTests
{
    /// <summary>Both real HTTP creation paths preserve original clocks and secondary metadata across Web correction/read/export.</summary>
    [PostgresTheory]
    [InlineData("check-in", "Europe/Athens", "2026-11-08T14:00:00", 7200)]
    [InlineData("log-location", "GMT+03:00", "2026-11-08T15:00:00", 10800)]
    public async Task FourClocksAndMetadataSurviveCrossClientCorrection(string route, string zone, string local, int offset)
    {
        var owner = await OwnerAsync();
        await using var db = fixture.CreateContext();
        var visits = new Mock<IPlaceVisitDetectionService>();
        await using var app = await HostAsync(db, visits);
        using var client = Client(app, owner);
        var key = Guid.NewGuid(); client.DefaultRequestHeaders.Add("Idempotency-Key", key.ToString("D"));
        var body = $$"""
            {"latitude":0,"longitude":0,"timestamp":"2026-11-08T12:59:59.999999950+03:00",
            "originalLocalTimestamp":"{{local}}","originalTimeZoneId":"{{zone}}","originalUtcOffsetSeconds":{{offset}},
            "submittedAtUtc":"2026-11-08T12:59:59.999999950+03:00","observedAtUtc":"2026-11-08T12:59:59.999999950+03:00",
            "observedAgeAtEventMs":0,"coordinateOrigin":"manual-map","accuracy":0,"altitude":0,"speed":0,"bearing":0,
            "appVersion":"1.0","appBuild":"0007","deviceModel":"model","osVersion":"os","provider":"gps",
            "batteryLevel":0,"isCharging":false,"isUserInvoked":false,"notes":"<p>EN / Αθήνα<br>line</p>"}
            """;
        using var response = await PostAsync(client, route, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = await db.Locations.AsNoTracking().SingleAsync(row => row.UserId == owner);
        Assert.Equal("2026-11-08T09:59:59.999999Z", ObservationEvidence.Format(new(row.LocalTimestamp)));
        Assert.Equal(local, row.OriginalLocalTimestamp); Assert.Equal(zone, row.OriginalTimeZoneId); Assert.Equal(offset, row.OriginalUtcOffsetSeconds);
        Assert.Equal(row.LocalTimestamp, row.SubmittedAtUtc!.Value.UtcDateTime); Assert.Equal(row.LocalTimestamp, row.ObservedAtUtc!.Value.UtcDateTime);
        var detail = await ProtectedLocationRequest.DetailAsync(client, row.Id);
        Assert.Equal(zone, detail.GetProperty("record").GetProperty("originalTimeZoneId").GetString());
        Assert.False(detail.GetProperty("record").GetProperty("isCharging").GetBoolean());
        await WebCookieAsync(client, app.Services, owner);
        var edit = await client.GetStringAsync($"/User/Location/Edit/{row.Id}");
        Assert.Contains("EN /", edit);
        var command = await ProtectedLocationRequest.CreateAsync(client, row.Id, new { notes = new { op = "set", html = "<p>Web correction</p>" } }, "web-edit");
        using var corrected = await command.SendAsync(client);
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        foreach (var format in new[] { "Csv", "GeoJson" })
        {
            using var exported = await client.GetAsync("/LocationExport/" + format);
            exported.EnsureSuccessStatusCode();
            await using var stream = await exported.Content.ReadAsStreamAsync();
            ILocationDataParser parser = format == "Csv" ? new CsvLocationParser(Microsoft.Extensions.Logging.Abstractions.NullLogger<CsvLocationParser>.Instance)
                : new WayfarerGeoJsonParser(Microsoft.Extensions.Logging.Abstractions.NullLogger<WayfarerGeoJsonParser>.Instance);
            var parsed = new List<Location>(); await foreach (var parsedRow in parser.ParseAsync(stream, owner)) parsed.Add(parsedRow);
            var value = Assert.Single(parsed);
            Assert.Equal(key, value.IdempotencyKey); Assert.Equal(zone, value.OriginalTimeZoneId); Assert.Equal(offset, value.OriginalUtcOffsetSeconds);
            Assert.Equal(local, value.OriginalLocalTimestamp); Assert.Equal(row.SubmittedAtUtc, value.SubmittedAtUtc);
            Assert.Equal("0007", value.AppBuild); Assert.Equal("model", value.DeviceModel); Assert.Equal("os", value.OsVersion);
            Assert.Equal("gps", value.Provider); Assert.Equal("1.0", value.AppVersion); Assert.False(value.IsCharging); Assert.Equal(0, value.BatteryLevel);
            Assert.Equal(0, value.Altitude); Assert.Equal(0, value.Speed); Assert.Equal(0, value.Bearing); Assert.False(value.IsUserInvoked);
            Assert.Equal("<p>Web correction</p>", value.Notes);
        }
        // New original facts participate in raw deletion consent even though effective editors cannot alter them.
        var oldDelete = await ProtectedLocationRequest.CreateAsync(client, row.Id, null);
        await db.Locations.Where(value => value.Id == row.Id).ExecuteUpdateAsync(setters => setters.SetProperty(value => value.SubmittedAtUtc, row.SubmittedAtUtc!.Value.AddSeconds(1)));
        using (var staleDelete = await oldDelete.SendAsync(client)) Assert.Equal(HttpStatusCode.Conflict, staleDelete.StatusCode);
        Assert.True(await db.Locations.AnyAsync(value => value.Id == row.Id));
        // Malformed original-zone evidence is rejected at real JSON validation, never silently nulled.
        client.DefaultRequestHeaders.Authorization = new("Bearer", owner);
        client.DefaultRequestHeaders.Remove("Idempotency-Key"); client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var invalid = await PostAsync(client, route, body.Replace(zone, "UTC\\n"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    /// <summary>Commit/lost reply, changed form, original retry and deliberate protected correction preserve one first winner.</summary>
    [PostgresFact]
    public async Task WebCreationRetryRetainsUnsavedDifferencesAndDeletionIsTerminal()
    {
        var owner = await OwnerAsync(); await using var db = fixture.CreateContext();
        var visits = new Mock<IPlaceVisitDetectionService>(MockBehavior.Strict);
        await using var app = await HostAsync(db, visits);
        using var client = Client(app, owner); await WebCookieAsync(client, app.Services, owner);
        var initial = await client.GetStringAsync("/User/Location/Create");
        var values = new Dictionary<string, string> { ["CreateKey"] = Input(initial, "CreateKey"), ["CreateIntent"] = Input(initial, "CreateIntent"),
            ["__RequestVerificationToken"] = Input(initial, "__RequestVerificationToken"), ["Latitude"] = "0", ["Longitude"] = "0",
            ["LocalTimestamp"] = "2026-11-08T12:00:00", ["Notes"] = "<p>first</p>", ["Address"] = " Manual " };
        using (var invalid = await client.PostAsync("/User/Location/Create", new FormUrlEncodedContent(values.Where(p => p.Key != "Latitude"))))
        {
            var html = await invalid.Content.ReadAsStringAsync(); Assert.Contains("field-validation-error", html);
            Assert.Equal(values["CreateKey"], Input(html, "CreateKey")); Assert.Contains("did not create", html);
        }
        // The reply is deliberately ignored; the browser still holds the original attempted values.
        using (var lost = await client.PostAsync("/User/Location/Create", new FormUrlEncodedContent(values))) Assert.Equal(HttpStatusCode.OK, lost.StatusCode);
        var row = await db.Locations.AsNoTracking().SingleAsync(row => row.UserId == owner);
        values["AttemptedValues"] = JsonSerializer.Serialize(new { latitude = 0, longitude = 0, localTimestamp = "2026-11-08T12:00:00",
            notes = "<p>first</p>", address = " Manual " });
        values["CreateMayHaveEffect"] = "true"; values["Notes"] = "<p>newer Αλλαγή</p>";
        using (var replay = await client.PostAsync("/User/Location/Create", new FormUrlEncodedContent(values)))
        {
            var html = await replay.Content.ReadAsStringAsync(); Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Contains("current form changes were not applied", html); Assert.Contains("Actual saved location", html);
            Assert.Contains("&lt;p&gt;newer", html); Assert.Contains("<p>first</p>", html); Assert.Contains("Open saved Location / Edit", html);
        }
        // Clearing an originally manual address is an authored difference, even when every current address field is blank.
        values["Notes"] = "<p>first</p>"; values["Address"] = "";
        using (var cleared = await client.PostAsync("/User/Location/Create", new FormUrlEncodedContent(values)))
            Assert.Contains("current form changes were not applied", await cleared.Content.ReadAsStringAsync());
        values["Notes"] = "<p>newer Αλλαγή</p>"; values["Address"] = " Manual ";
        Assert.Equal(1, await db.Locations.CountAsync(row => row.UserId == owner));
        Assert.Equal("Manual", row.Address); Assert.Equal("web-create", row.Source); Assert.True(row.IsUserInvoked);
        Assert.Equal("manual-map", row.CoordinateOrigin); Assert.Null(row.PresenceObservedAtUtc); Assert.Null(row.ObservedAtUtc);
        Assert.Null(row.OriginalTimeZoneId); Assert.Null(row.SubmittedAtUtc); Assert.Null(row.DeviceModel); visits.VerifyNoOtherCalls();
        var edit = await ProtectedLocationRequest.CreateAsync(client, row.Id, new { notes = new { op = "set", html = "<p>newer Αλλαγή</p>" } });
        using (var corrected = await edit.SendAsync(client)) Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        Assert.Contains("newer", await client.GetStringAsync($"/User/Location/Edit/{row.Id}"));
        var delete = await ProtectedLocationRequest.CreateAsync(client, row.Id, null);
        using (var deleted = await delete.SendAsync(client)) Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        using (var replay = await client.PostAsync("/User/Location/Create", new FormUrlEncodedContent(values)))
            Assert.Contains("This retry cannot restore", await replay.Content.ReadAsStringAsync());
        Assert.False(await db.Locations.AnyAsync(row => row.UserId == owner));
        var separate = await client.GetStringAsync("/User/Location/Create"); Assert.NotEqual(values["CreateKey"], Input(separate, "CreateKey"));
        var other = await OwnerAsync(); using var otherClient = Client(app, other); await WebCookieAsync(otherClient, app.Services, other);
        var otherForm = await otherClient.GetStringAsync("/User/Location/Create"); values["__RequestVerificationToken"] = Input(otherForm, "__RequestVerificationToken");
        using var foreign = await otherClient.PostAsync("/User/Location/Create", new FormUrlEncodedContent(values));
        Assert.Equal(HttpStatusCode.Redirect, foreign.StatusCode);
        Assert.Contains("AccessDenied", foreign.Headers.Location!.ToString());
    }

    /// <summary>Blank authored Notes must confirm the real save and replay without hiding genuine newer differences.</summary>
    [PostgresFact]
    public async Task WebBlankNotesConfirmsSaveAndReplayWithManualAddress()
    {
        var owner = await OwnerAsync();
        await using var db = fixture.CreateContext();
        await using var app = await HostAsync(db, new Mock<IPlaceVisitDetectionService>(MockBehavior.Strict));
        using var client = Client(app, owner);
        await WebCookieAsync(client, app.Services, owner);
        var initial = await client.GetStringAsync("/User/Location/Create");
        var values = new Dictionary<string, string> { ["CreateKey"] = Input(initial, "CreateKey"), ["CreateIntent"] = Input(initial, "CreateIntent"),
            ["__RequestVerificationToken"] = Input(initial, "__RequestVerificationToken"), ["Latitude"] = "0", ["Longitude"] = "0",
            ["LocalTimestamp"] = "2026-11-08T12:00:00", ["Notes"] = "", ["Address"] = "Manual",
            ["AttemptedValues"] = JsonSerializer.Serialize(new { latitude = 0, longitude = 0, localTimestamp = "2026-11-08T12:00:00", notes = "", address = "Manual" }) };
        using (var created = await client.PostAsync("/User/Location/Create", new FormUrlEncodedContent(values)))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            Assert.Contains("The location was saved.", await created.Content.ReadAsStringAsync());
        }
        values["CreateMayHaveEffect"] = "true";
        using (var replay = await client.PostAsync("/User/Location/Create", new FormUrlEncodedContent(values)))
            Assert.Contains("The original creation was found.", await replay.Content.ReadAsStringAsync());
        values["Notes"] = "<p>newer</p>";
        using (var changed = await client.PostAsync("/User/Location/Create", new FormUrlEncodedContent(values)))
        {
            var html = await changed.Content.ReadAsStringAsync();
            Assert.Contains("current form changes were not applied", html);
            Assert.Contains("&lt;p&gt;newer", html);
        }
        var row = await db.Locations.AsNoTracking().SingleAsync(row => row.UserId == owner);
        Assert.Equal("", row.Notes);
        Assert.Equal("Manual", row.Address);
        Assert.Equal(Guid.Parse(values["CreateKey"]), row.IdempotencyKey);
        // Explicit protected clear is null, and must still differ from the browser's empty authored text.
        var clear = await ProtectedLocationRequest.CreateAsync(client, row.Id, new { notes = new { op = "clear" } });
        using (var cleared = await clear.SendAsync(client)) Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        values["Notes"] = "";
        using (var clearedWinner = await client.PostAsync("/User/Location/Create", new FormUrlEncodedContent(values)))
            Assert.Contains("current form changes were not applied", await clearedWinner.Content.ReadAsStringAsync());
        Assert.Null((await db.Locations.AsNoTracking().SingleAsync(row => row.UserId == owner)).Notes);
    }

    /// <summary>API, typed storage/detail, canonical files and production import retain known-empty versus known-null origin.</summary>
    [PostgresTheory]
    [InlineData("Csv", "")]
    [InlineData("GeoJson", "")]
    [InlineData("Csv", null)]
    [InlineData("GeoJson", null)]
    public async Task NativeOriginStateSurvivesApiExportAndImport(string format, string? origin)
    {
        var owner = await OwnerAsync();
        var destination = await OwnerAsync();
        await using var db = fixture.CreateContext();
        await using var app = await HostAsync(db, new Mock<IPlaceVisitDetectionService>(MockBehavior.Strict));
        using var client = Client(app, owner);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var created = await PostAsync(client, "check-in", JsonSerializer.Serialize(new { latitude = 0, longitude = 0,
            timestamp = "1960-01-01T00:00:00.000000999Z", coordinateOrigin = origin, originalTimeZoneId = "UTC-02:30" }));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var row = await db.Locations.AsNoTracking().SingleAsync(row => row.UserId == owner);
        Assert.Equal(origin, row.CoordinateOrigin);
        var detail = await ProtectedLocationRequest.DetailAsync(client, row.Id);
        Assert.Equal(origin, detail.GetProperty("record").GetProperty("coordinateOrigin").GetString());
        await WebCookieAsync(client, app.Services, owner);
        using var exported = await client.GetAsync("/LocationExport/" + format);
        exported.EnsureSuccessStatusCode();
        var payload = await exported.Content.ReadAsByteArrayAsync();
        using var stream = new MemoryStream(payload);
        var fileType = format == "Csv" ? LocationImportFileType.Csv : LocationImportFileType.WayfarerGeoJson;
        var parser = new LocationDataParserFactory(NullLoggerFactory.Instance).GetParser(fileType);
        var rows = new List<Location>();
        await foreach (var value in parser.ParseAsync(stream, destination)) rows.Add(value);
        var parsed = Assert.Single(rows);
        Assert.Equal(row.IdempotencyKey, parsed.IdempotencyKey);
        Assert.Equal(origin, parsed.CoordinateOrigin);
        var path = ImportStaging.TempFile();
        try
        {
            await File.WriteAllBytesAsync(path, payload);
            var import = new LocationImport { UserId = destination, FilePath = path, FileType = fileType,
                Status = ImportStatus.InProgress, ExecutionEpoch = 7, TotalRecords = 0, LastProcessedIndex = 0 };
            db.LocationImports.Add(import);
            await db.SaveChangesAsync();
            using var http = new HttpClient(new NoNetwork());
            var worker = new LocationImportService(ImportStaging.Files, db,
                new ReverseGeocodingService(http, NullLogger<BaseApiController>.Instance), NullLogger<LocationImportService>.Instance,
                new LocationDataParserFactory(NullLoggerFactory.Instance), new SseService());
            Assert.Equal(LocationImportExecutionOutcome.Completed,
                await worker.ProcessImportExecution(import.Id, import.ExecutionEpoch, CancellationToken.None));
            var imported = await db.Locations.AsNoTracking().SingleAsync(value => value.UserId == destination);
            Assert.Equal(origin, imported.CoordinateOrigin);
            Assert.Equal(row.IdempotencyKey, imported.IdempotencyKey);
            Assert.NotEqual(row.Id, imported.Id);
            Assert.Equal(row.LocalTimestamp, imported.LocalTimestamp);
            Assert.Equal(row.OriginalTimeZoneId, imported.OriginalTimeZoneId);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Reads the existing MVC escaped hidden inputs rather than inventing form credentials.</summary>
    private static string Input(string html, string name)
    {
        var input = Regex.Match(html, "<input[^>]*name=\"" + name + "\"[^>]*>").Value;
        return WebUtility.HtmlDecode(Regex.Match(input, "value=\"([^\"]*)\"").Groups[1].Value);
    }

    /// <summary>Uses real Identity and antiforgery cookies for owner Web forms and protected edits.</summary>
    private static async Task WebCookieAsync(HttpClient client, IServiceProvider services, string owner)
    {
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Add("Cookie", IdentityRouteHost.Cookie(services, owner));
        var token = await IdentityRouteHost.AntiforgeryAsync(client);
        var csrf = client.DefaultRequestHeaders.GetValues("Cookie").Single();
        client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Add("Cookie", csrf + "; " + IdentityRouteHost.Cookie(services, owner));
        client.DefaultRequestHeaders.Remove("RequestVerificationToken"); client.DefaultRequestHeaders.Add("RequestVerificationToken", token);
    }
}
