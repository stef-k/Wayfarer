using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using NetTopologySuite.Geometries;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Coordinate presence and geography through production MVC binding and real owner persistence.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationCoordinatesPostgresTests(PostgresImportTestFixture fixture) : TestBase
{
    private const string Event = "2026-08-22T08:00:00Z";

    /// <summary>Origin is stored as geography, with honest capture facts and no required address or live effects.</summary>
    [PostgresTheory]
    [InlineData("check-in", 0, 0)]
    [InlineData("log-location", 0, 0)]
    [InlineData("check-in", 0, 23)]
    [InlineData("log-location", 0, 23)]
    [InlineData("check-in", 38, 0)]
    [InlineData("log-location", 38, 0)]
    public async Task ExplicitZeroPersistsAndReturnsInOwnerHistory(string route, int lat, int lon)
    {
        var owner = await OwnerAsync();
        await using var db = fixture.CreateContext();
        var visits = new Mock<IPlaceVisitDetectionService>(MockBehavior.Strict);
        if (route == "log-location")
            visits.Setup(service => service.ProcessPingAsync(owner, It.IsAny<Point>(), null, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        await using var app = await HostAsync(db, visits);
        using var client = Client(app, owner);
        var key = Guid.NewGuid();
        client.DefaultRequestHeaders.Add("Idempotency-Key", key.ToString());
        // Exercise coordinate-local timestamp conversion at origin as well as explicit UTC binding.
        var clock = route == "log-location" && lat == 0 && lon == 0 ? "2026-08-22T08:00:00" : Event;
        var before = DateTime.UtcNow;
        using var response = await PostAsync(client, route,
            $$"""{"latitude":{{lat}},"longitude":{{lon}},"timestamp":"{{clock}}","coordinateOrigin":"manual-map","isUserInvoked":true}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var verify = fixture.CreateContext();
        var row = await verify.Locations.SingleAsync(row => row.UserId == owner);
        Assert.Equal(lat, row.Coordinates.Y);
        Assert.Equal(lon, row.Coordinates.X);
        Assert.Equal(4326, row.Coordinates.SRID);
        Assert.Equal(key, row.IdempotencyKey);
        Assert.Equal(DateTime.Parse(Event).ToUniversalTime(), row.LocalTimestamp);
        Assert.Equal(DateTimeKind.Utc, row.LocalTimestamp.Kind);
        Assert.InRange(row.Timestamp, before.AddMilliseconds(-1), DateTime.UtcNow);
        Assert.Null(row.Address);
        Assert.Null(row.PresenceObservedAtUtc);
        // Automatic arrival Visit evaluation is unchanged; map-selected check-ins assert no sensor presence.
        if (route == "log-location")
            visits.Verify(service => service.ProcessPingAsync(owner, It.IsAny<Point>(), null, It.IsAny<CancellationToken>()), Times.Once);
        visits.VerifyNoOtherCalls();
        if (lat != 0 || lon != 0) return;

        Assert.Equal("UTC", row.TimeZoneId);
        Assert.Equal("UTC", row.CaptureTimeZoneId);
        Assert.Equal(0, row.CaptureOffsetSeconds);
        using var detailResponse = await client.GetAsync($"/api/location/history/{row.Id}?viewerZone=UTC");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        var record = detail.RootElement.GetProperty("record");
        Assert.Equal("2026-08-22T08:00:00.000000Z", record.GetProperty("eventUtc").GetString());
        Assert.Equal(0, record.GetProperty("position").GetProperty("latitude").GetDouble());
        Assert.Equal(0, record.GetProperty("position").GetProperty("longitude").GetDouble());
        Assert.Equal("available", record.GetProperty("captureTime").GetProperty("status").GetString());
        Assert.Equal("UTC", record.GetProperty("captureTime").GetProperty("zoneId").GetString());
        Assert.Equal(0, record.GetProperty("captureTime").GetProperty("offsetSeconds").GetInt32());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("address").ValueKind);
        Assert.Equal(JsonValueKind.Null, record.GetProperty("presenceObservedAtUtc").ValueKind);
        using var history = await client.GetAsync("/api/location/history?timeBasis=utc&timeMode=interval&viewerZone=UTC&startUtc=2026-08-22T00:00:00Z&endUtc=2026-08-23T00:00:00Z");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using var page = JsonDocument.Parse(await history.Content.ReadAsStringAsync());
        Assert.Equal(row.Id, Assert.Single(page.RootElement.GetProperty("items").EnumerateArray()).GetProperty("locationId").GetInt32());
    }

    /// <summary>Invalid new JSON coordinates never create history, request enrichment or process Visits.</summary>
    [PostgresTheory]
    [InlineData("check-in")]
    [InlineData("log-location")]
    public async Task InvalidNewCoordinatesCannotCreateOrProduceEffects(string route)
    {
        var owner = await OwnerAsync();
        await using var db = fixture.CreateContext();
        var visits = new Mock<IPlaceVisitDetectionService>(MockBehavior.Strict);
        await using var app = await HostAsync(db, visits);
        using var client = Client(app, owner);
        foreach (var coordinates in InvalidCoordinates())
        {
            using var response = await PostAsync(client, route, Payload(coordinates));
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
                $"{route}: {coordinates}: {response.StatusCode}");
            Assert.False(await db.Locations.AnyAsync(row => row.UserId == owner));
            visits.VerifyNoOtherCalls();
        }
    }

    /// <summary>Successfully bound incomplete retries retain winner/tombstone priority and account isolation.</summary>
    [PostgresTheory]
    [InlineData("check-in")]
    [InlineData("log-location")]
    public async Task BoundRetriesResolveOnlyTheOwnersWinnerOrDeletion(string route)
    {
        var owner = await OwnerAsync();
        var other = await OwnerAsync();
        await using var db = fixture.CreateContext();
        await using var app = await HostAsync(db, new Mock<IPlaceVisitDetectionService>(MockBehavior.Strict));
        using var client = Client(app, owner);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var created = await PostAsync(client, route, Payload("\"latitude\":0,\"longitude\":0"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var row = await db.Locations.SingleAsync(row => row.UserId == owner);
        foreach (var incomplete in new[] { "", "\"latitude\":null,\"longitude\":null", "\"latitude\":0" })
        {
            using var replay = await PostAsync(client, route, Payload(incomplete));
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        }
        // A type error still fails transport binding, even when the key already has a winner.
        using var malformed = await PostAsync(client, route, Payload("\"latitude\":\"bad\",\"longitude\":0"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", other);
        using var foreign = await PostAsync(client, route, Payload(""));
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", owner);
        using var deleted = await client.DeleteAsync($"/api/location/{row.Id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        foreach (var incomplete in new[] { "", "\"latitude\":null,\"longitude\":null" })
        {
            using var retry = await PostAsync(client, route, Payload(incomplete));
            Assert.Equal(HttpStatusCode.Gone, retry.StatusCode);
            Assert.Contains("location_deleted", await retry.Content.ReadAsStringAsync());
        }
        using var deletedMalformed = await PostAsync(client, route, "{");
        Assert.Equal(HttpStatusCode.BadRequest, deletedMalformed.StatusCode);
        await using var verify = fixture.CreateContext();
        Assert.False(await verify.Locations.AnyAsync(row => row.UserId == owner || row.UserId == other));
    }

    /// <summary>Origin geography retains automatic accuracy filtering and a truthful unstored skip response.</summary>
    [PostgresFact]
    public async Task AutomaticOriginStillSkipsPoorAccuracy()
    {
        var owner = await OwnerAsync();
        await using var db = fixture.CreateContext();
        var visits = new Mock<IPlaceVisitDetectionService>(MockBehavior.Strict);
        await using var app = await HostAsync(db, visits);
        using var client = Client(app, owner);
        using var response = await PostAsync(client, "log-location",
            $$"""{"latitude":0,"longitude":0,"accuracy":10000,"timestamp":"{{Event}}","isUserInvoked":false}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("skipped").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("locationId").ValueKind);
        Assert.False(await db.Locations.AnyAsync(row => row.UserId == owner));
        visits.VerifyNoOtherCalls();
    }

    /// <summary>Cookie and bearer edits preserve omitted/null coordinates and reject invalid pairs atomically.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialUpdatesPreserveOrReplaceTheWholePosition(bool cookie)
    {
        var owner = await OwnerAsync();
        await using var db = fixture.CreateContext();
        await using var app = await HostAsync(db, new Mock<IPlaceVisitDetectionService>(MockBehavior.Strict));
        using var client = Client(app, owner);
        using var created = await PostAsync(client, "check-in", Payload("\"latitude\":10,\"longitude\":20"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var row = await db.Locations.SingleAsync(row => row.UserId == owner);
        if (cookie)
        {
            client.DefaultRequestHeaders.Authorization = null;
            client.DefaultRequestHeaders.Add("Cookie", IdentityRouteHost.Cookie(app.Services, owner));
            var token = await IdentityRouteHost.AntiforgeryAsync(client);
            var csrfCookie = client.DefaultRequestHeaders.GetValues("Cookie").Single();
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Add("Cookie", csrfCookie + "; " + IdentityRouteHost.Cookie(app.Services, owner));
            client.DefaultRequestHeaders.Add("RequestVerificationToken", token);
        }
        foreach (var coordinates in new[] { "", "\"latitude\":null,\"longitude\":null" })
        {
            using var notes = await client.PutAsync($"/api/location/{row.Id}", Json(Payload(coordinates, "kept")));
            Assert.Equal(HttpStatusCode.OK, notes.StatusCode);
            await AssertStoredAsync(row.Id, 10, 20, "kept");
        }
        foreach (var coordinates in InvalidCoordinates().Where(value => value.Length != 0 && value != "\"latitude\":null,\"longitude\":null"))
        {
            using var invalid = await client.PutAsync($"/api/location/{row.Id}", Json(Payload(coordinates, "must not apply")));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            await AssertStoredAsync(row.Id, 10, 20, "kept");
        }
        using var origin = await client.PutAsync($"/api/location/{row.Id}", Json(Payload("\"latitude\":0,\"longitude\":0", "origin")));
        Assert.Equal(HttpStatusCode.OK, origin.StatusCode);
        await AssertStoredAsync(row.Id, 0, 0, "origin");
    }

    /// <summary>Actual antiforgery-protected Web forms bind both axes and show field errors without saving invalid edits.</summary>
    [PostgresTheory]
    [InlineData("Create")]
    [InlineData("Edit")]
    public async Task WebFormsRequireValidAxesAndAcceptOrigin(string action)
    {
        var owner = await OwnerAsync();
        await using var db = fixture.CreateContext();
        await using var app = await HostAsync(db, new Mock<IPlaceVisitDetectionService>(MockBehavior.Strict));
        using var client = Client(app, owner);
        using var seeded = await PostAsync(client, "check-in", Payload("\"latitude\":10,\"longitude\":20", "kept"));
        Assert.Equal(HttpStatusCode.OK, seeded.StatusCode);
        var row = await db.Locations.SingleAsync(row => row.UserId == owner);
        client.DefaultRequestHeaders.Authorization = null;
        // Generate the antiforgery token for the authenticated identity, just like a rendered form.
        client.DefaultRequestHeaders.Add("Cookie", IdentityRouteHost.Cookie(app.Services, owner));
        var token = await IdentityRouteHost.AntiforgeryAsync(client);
        var csrfCookie = client.DefaultRequestHeaders.GetValues("Cookie").Single();
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", csrfCookie + "; " + IdentityRouteHost.Cookie(app.Services, owner));
        if (action == "Create")
        {
            var initial = await client.GetStringAsync("/User/Location/Create");
            foreach (var axis in new[] { "Latitude", "Longitude" })
            {
                var input = Regex.Match(initial, $"<input[^>]*id=\"{axis}\"[^>]*>").Value;
                Assert.NotEmpty(input);
                Assert.DoesNotContain("value=\"0\"", input);
            }
        }
        foreach (var pair in new[] { (Lat: (string?)null, Lon: (string?)null), (null, "0"), ("0", null),
                     ("", "0"), ("0", ""), ("NaN", "0"), ("0", "NaN"), ("Infinity", "0"),
                     ("91", "0"), ("0", "-181"), ("bad", "0") })
        {
            using var response = await client.PostAsync($"/User/Location/{action}", Form(pair.Item1, pair.Item2, token, row.Id));
            var html = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{pair}: {response.StatusCode}: {html.Split("HEADERS")[0]}");
            Assert.Contains("field-validation-error", html);
            Assert.Contains(pair.Item1 == "0" ? "data-valmsg-for=\"Longitude\"" : "data-valmsg-for=\"Latitude\"", html);
            await AssertStoredAsync(row.Id, 10, 20, "kept");
            Assert.Equal(1, await db.Locations.CountAsync(item => item.UserId == owner));
        }
        using var valid = await client.PostAsync($"/User/Location/{action}", Form("0", "0", token, row.Id));
        Assert.Equal(HttpStatusCode.Redirect, valid.StatusCode);
        await using var verify = fixture.CreateContext();
        var saved = action == "Edit" ? await verify.Locations.SingleAsync(item => item.Id == row.Id)
            : await verify.Locations.SingleAsync(item => item.UserId == owner && item.Id != row.Id);
        Assert.Equal(0, saved.Coordinates.X);
        Assert.Equal(0, saved.Coordinates.Y);
        Assert.Null(saved.PresenceObservedAtUtc);
    }

    /// <summary>Uses the production MVC/Identity registrations and replaces external effects only.</summary>
    private Task<WebApplication> HostAsync(ApplicationDbContext db, Mock<IPlaceVisitDetectionService> visits)
    {
        var reverse = new ReverseGeocodingService(new HttpClient(new NoNetwork()), NullLogger<BaseApiController>.Instance);
        var settings = new Mock<IApplicationSettingsService>();
        settings.Setup(service => service.GetSettings()).Returns(new ApplicationSettings { Id = 1 });
        return IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.RemoveAll<ApplicationDbContext>();
            services.AddScoped(_ => fixture.CreateContext());
            services.AddScoped(provider => ApiLocationAdmissionTests.Controller(provider.GetRequiredService<ApplicationDbContext>(),
                new ApiWorkAdmission(64, 8), visits.Object, reverse: reverse));
            services.AddSingleton(settings.Object);
            services.AddSingleton(reverse);
            services.AddSingleton(visits.Object);
            services.AddControllers().AddControllersAsServices();
        });
    }

    /// <summary>Creates fixture-owned accounts and real bearer tokens without production data.</summary>
    private async Task<string> OwnerAsync()
    {
        var owner = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        db.ApiTokens.Add(new ApiToken { User = null!, UserId = owner.Id, Name = "coordinates", TokenHash = ApiTokenService.HashToken(owner.Id) });
        await db.SaveChangesAsync();
        return owner.Id;
    }

    /// <summary>A fresh context proves invalid requests did not partially persist edits.</summary>
    private async Task AssertStoredAsync(int id, double latitude, double longitude, string notes)
    {
        await using var db = fixture.CreateContext();
        var row = await db.Locations.SingleAsync(row => row.Id == id);
        Assert.Equal(latitude, row.Coordinates.Y);
        Assert.Equal(longitude, row.Coordinates.X);
        Assert.Contains(notes, row.Notes);
    }

    /// <summary>Includes transport errors and successfully bound invalid-new-work cases on either axis.</summary>
    private static IEnumerable<string> InvalidCoordinates()
    {
        yield return "";
        yield return "\"latitude\":null,\"longitude\":null";
        foreach (var axis in new[] { "latitude", "longitude" })
        {
            var other = axis == "latitude" ? "longitude" : "latitude";
            yield return $"\"{other}\":0";
            yield return $"\"{other}\":10";
            foreach (var value in new[] { "null", "\"\"", "\" \"", "\"bad\"", "\"NaN\"", "\"Infinity\"", "\"-Infinity\"", "1e999", axis == "latitude" ? "91" : "181", axis == "latitude" ? "-91" : "-181" })
                yield return $"\"{axis}\":{value},\"{other}\":0";
        }
    }

    /// <summary>Builds real full-form bodies, preserving the distinction between absent and blank fields.</summary>
    private static FormUrlEncodedContent Form(string? latitude, string? longitude, string token, int id)
    {
        var values = new Dictionary<string, string> { ["Id"] = id.ToString(), ["__RequestVerificationToken"] = token,
            ["LocalTimestamp"] = "2026-08-22T08:00:00", ["Notes"] = "changed" };
        if (latitude != null) values["Latitude"] = latitude;
        if (longitude != null) values["Longitude"] = longitude;
        return new(values);
    }

    /// <summary>Creates a raw JSON body; no DTO serializer can fill absent coordinate fields.</summary>
    private static string Payload(string coordinates, string notes = "") =>
        $$"""{ {{(coordinates.Length == 0 ? "" : coordinates + ",")}} "timestamp":"{{Event}}","coordinateOrigin":"manual-map","notes":"{{notes}}" }""";
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string route, string body) => client.PostAsync($"/api/location/{route}", Json(body));
    private static HttpClient Client(WebApplication app, string owner)
    {
        var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://wayfarer.test");
        client.DefaultRequestHeaders.Authorization = new("Bearer", owner);
        return client;
    }

    /// <summary>Any accidental provider transport in this controlled cohort fails immediately.</summary>
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Provider network is forbidden in coordinate tests.");
    }
}
