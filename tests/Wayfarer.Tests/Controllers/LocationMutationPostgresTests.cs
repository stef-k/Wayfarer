using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Services.LocationMutations;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Moq;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>A1–A9/A13–A14 through actual routed commands and the existing guarded PostgreSQL fixture.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed partial class LocationMutationPostgresTests(PostgresMigrationTestFixture fixture) : IClassFixture<PostgresMigrationTestFixture>
{
    private IServiceProvider hostServices = null!;
    private readonly IDataProtectionProvider protection = new EphemeralDataProtectionProvider();

    /// <summary>Same selected Notes stale, disjoint Activity succeeds, and invalid combined selection rolls back all fields.</summary>
    [PostgresFact]
    public async Task SelectedOriginalsAndInvalidActivityAreAtomic()
    {
        var (owner, credential, id) = await SeedAsync();
        int activityId;
        await using (var catalog = fixture.CreateContext())
        {
            var selected = new ActivityType { Name = "Protected activity " + Guid.NewGuid().ToString("N") };
            catalog.Add(selected); await catalog.SaveChangesAsync(); activityId = selected.Id;
        }
        await WithHostAsync(credential, async client =>
        {
            var detail = await ProtectedLocationRequest.DetailAsync(client, id);
            var first = await ProtectedLocationRequest.CreateAsync(client, id, new { notes = new { op = "set", html = "N1" } }, detail: detail);
            var stale = await ProtectedLocationRequest.CreateAsync(client, id, new { notes = new { op = "clear" } }, detail: detail);
            var activity = await ProtectedLocationRequest.CreateAsync(client, id, new { activity = new { op = "set", id = activityId } }, detail: detail);
            await AssertStateAsync(await first.SendAsync(client), 200, "applied");
            var rejection = await AssertStateAsync(await stale.SendAsync(client), 409, "rejected", "locationBaseStale");
            Assert.Equal("notesHtml", Assert.Single(rejection.GetProperty("staleGroups").EnumerateArray()).GetString());
            await AssertStateAsync(await activity.SendAsync(client), 200, "applied");
            var invalid = await ProtectedLocationRequest.CreateAsync(client, id, new { notes = new { op = "set", html = "must not persist" }, activity = new { op = "set", id = int.MaxValue } });
            await AssertStateAsync(await invalid.SendAsync(client), 422, "rejected", "activityInvalid");
            await using var db = fixture.CreateContext();
            var row = await db.Locations.SingleAsync(row => row.Id == id);
            Assert.Equal(RichNotes.Normalize("N1"), row.Notes); Assert.Equal(activityId, row.ActivityTypeId);
            Assert.Equal(4, await db.LocationMutationOperations.CountAsync(row => row.UserId == owner));
        });
    }

    /// <summary>A real effect/receipt commit with a lost COMMIT acknowledgement recovers without a second patch.</summary>
    [PostgresFact]
    public async Task LostCommitAcknowledgementThenNewerCorrectionKeepsHistoricalReceipt()
    {
        var (owner, credential, id) = await SeedAsync();
        var lost = new LostCommit();
        await WithHostAsync(credential, async client =>
        {
            var request = await ProtectedLocationRequest.CreateAsync(client, id, new { notes = new { op = "set", html = "first" } });
            var preSend = request.Fingerprint(owner);
            lost.Armed = true;
            using (var lostResponse = await request.SendAsync(client)) Assert.True((int)lostResponse.StatusCode >= 500);
            var recovered = await AssertStateAsync(await request.RecoverAsync(client), 200, "applied");
            Assert.Equal(preSend, recovered.GetProperty("fingerprint").GetString());
            var newer = await ProtectedLocationRequest.CreateAsync(client, id, new { notes = new { op = "set", html = "newer" } });
            await AssertStateAsync(await newer.SendAsync(client), 200, "applied");
            Assert.Equal(recovered.GetRawText(), (await AssertStateAsync(await request.SendAsync(client), 200, "applied")).GetRawText());
            using var changed = await (request with { Bytes = [.. request.Bytes, 10] }).SendAsync(client);
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
            Assert.Equal("operationIdentityReused", JsonSerializer.Deserialize<JsonElement>(await changed.Content.ReadAsStringAsync()).GetProperty("code").GetString());
            await using var verify = fixture.CreateContext();
            Assert.Equal(RichNotes.Normalize("newer"), (await verify.Locations.SingleAsync(row => row.Id == id)).Notes);
            Assert.Equal(2, await verify.LocationMutationOperations.CountAsync(row => row.UserId == owner));
        }, lost);
    }

    /// <summary>Position-only keeps exact stored capture ticks; denied optional enrichment completes movement and explicit manual Clear.</summary>
    [PostgresFact]
    public async Task CoordinateOnlyPreservesInstantAndManualIntentWithoutProvider()
    {
        var (_, credential, id) = await SeedAsync();
        DateTime original;
        await using (var db = fixture.CreateContext())
        {
            var row = await db.Locations.SingleAsync(row => row.Id == id);
            row.LocalTimestamp = DateTime.Parse("1999-12-31T23:59:59.123456Z", null, System.Globalization.DateTimeStyles.AdjustToUniversal);
            row.Country = "obsolete"; row.OriginalCreateAddressToken = Guid.NewGuid();
            await db.SaveChangesAsync(); original = row.LocalTimestamp;
        }
        await WithHostAsync(credential, async client =>
        {
            var request = await ProtectedLocationRequest.CreateAsync(client, id,
                new { position = new { op = "set", latitude = 40.7, longitude = -74.0 }, manualAddress = new { op = "set", fields = new { place = "  My place  ", country = (string?)null } } }, "web-edit");
            // Bearer cannot claim the Web-only adapter.
            using var forbidden = await request.SendAsync(client); Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            var ownerRequest = await ProtectedLocationRequest.CreateAsync(client, id, new { position = new { op = "set", latitude = 40.7, longitude = -74.0 } });
            var receipt = await AssertStateAsync(await ownerRequest.SendAsync(client), 200, "applied");
            Assert.Equal("unavailable", receipt.GetProperty("providerOutcome").GetString());
            await using var db = fixture.CreateContext();
            var current = await db.Locations.SingleAsync(row => row.Id == id);
            Assert.Equal(original.Ticks, current.LocalTimestamp.Ticks); Assert.Null(current.Country);
            Assert.Null(current.PresenceObservedAtUtc); Assert.Null(current.OriginalCreateAddressToken);
            Assert.Equal("America/New_York", current.TimeZoneId);
            await AssertStateAsync(await ownerRequest.SendAsync(client), 200, "applied");
        });
    }

    /// <summary>Raw damaged-row consent stales on raw changes, ignores derived changes, and commits marker/deletion/receipt together.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DamagedDeletionAndKeylessAbsenceStayScoped(bool keyed)
    {
        var (owner, credential, id) = await SeedAsync(keyed);
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE "Locations" SET "LocalTimestamp" = 'infinity', "Altitude" = 'NaN', "Coordinates" = ST_GeogFromText('SRID=4326;POINT EMPTY'),
                    "ReverseGeocodedAt" = '-infinity', "HistoryTimeVersion" = NULL, "HistoryTimeStatus" = NULL,
                    "HistoryTimeFailureCode" = NULL, "CaptureLocalTimestamp" = NULL, "CaptureTimeZoneId" = NULL, "CaptureOffsetSeconds" = NULL WHERE "Id" = {{id}}
                """);
        await WithHostAsync(credential, async client =>
        {
            var detail = await ProtectedLocationRequest.DetailAsync(client, id);
            var values = detail.GetProperty("locationMutation").GetProperty("baseValues");
            Assert.Equal("unavailable", values.GetProperty("position").GetProperty("state").GetString());
            Assert.Equal("known", values.GetProperty("deleteContent").GetProperty("state").GetString());
            var stale = await ProtectedLocationRequest.CreateAsync(client, id, null, detail: detail);
            await using (var db = fixture.CreateContext()) await db.Locations.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Notes, "changed raw"));
            await AssertStateAsync(await stale.SendAsync(client), 409, "rejected", "locationBaseStale");
            var deletion = await ProtectedLocationRequest.CreateAsync(client, id, null);
            await using (var db = fixture.CreateContext()) await db.Locations.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.OriginalCreateAddressToken, Guid.NewGuid()));
            await AssertStateAsync(await deletion.SendAsync(client), 200, "applied");
            await AssertStateAsync(await deletion.RecoverAsync(client), 200, "applied");
            await using var verify = fixture.CreateContext();
            Assert.False(await verify.Locations.AnyAsync(row => row.Id == id));
            Assert.Equal(keyed ? 1 : 0, await verify.Set<LocationDeletionMarker>().CountAsync(row => row.UserId == owner));
            using var absentRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/location/history/{id}?viewerZone=UTC");
            absentRequest.Headers.Add("X-Location-Base", deletion.Handle);
            using var absence = await client.SendAsync(absentRequest);
            Assert.Equal(HttpStatusCode.NotFound, absence.StatusCode);
            if (!keyed) Assert.Equal("location-target-absence-1", JsonSerializer.Deserialize<JsonElement>(await absence.Content.ReadAsStringAsync()).GetProperty("contract").GetString());
        });
    }

    /// <summary>Every test uses a fixture-owned account/key and exact microsecond instant.</summary>
    private async Task<(string Owner, string Token, int Id)> SeedAsync(bool keyed = true)
    {
        var user = await fixture.CreateUserAsync(); var credential = new ApiTokenService(null!, null!).GenerateToken();
        await using var db = fixture.CreateContext();
        db.ApiTokens.Add(new() { UserId = user.Id, User = null!, Name = "947-test", TokenHash = ApiTokenService.HashToken(credential) });
        var row = new Location { UserId = user.Id, IdempotencyKey = keyed ? Guid.NewGuid() : null, TimeZoneId = "Europe/Athens",
            Coordinates = new(23.72, 37.98) { SRID = 4326 }, LocalTimestamp = new(2026, 10, 29, 8, 15, 0, DateTimeKind.Utc), Timestamp = DateTime.UtcNow, Notes = "original" };
        db.Add(row); await db.SaveChangesAsync(); return (user.Id, credential, row.Id);
    }

    /// <summary>Reuses the production Identity/MVC host and substitutes only optional external transport.</summary>
    private async Task WithHostAsync(string credential, Func<HttpClient, Task> run, params IInterceptor[] interceptors)
    {
        var root = Path.Combine(Path.GetTempPath(), "wayfarer-947-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var seed = fixture.CreateContext();
            await using var app = await IdentityRouteHost.StartAsync(seed, root, services =>
            {
                services.RemoveAll<ApplicationDbContext>(); services.AddScoped(_ => fixture.CreateContext(interceptors));
                services.AddSingleton(protection);
                var settings = new Mock<IApplicationSettingsService>();
                settings.Setup(service => service.GetSettings()).Returns(new ApplicationSettings());
                services.AddSingleton(settings.Object);
                services.AddScoped(_ => new ReverseGeocodingService(new HttpClient(new NoProvider()), NullLogger<BaseApiController>.Instance));
                services.AddControllers().AddControllersAsServices();
            });
            using var client = app.GetTestClient(); client.BaseAddress = new("https://wayfarer.test");
            client.DefaultRequestHeaders.Authorization = new("Bearer", credential);
            hostServices = app.Services;
            await run(client);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Checks product envelope/status rather than assuming success from HTTP alone.</summary>
    private static async Task<JsonElement> AssertStateAsync(HttpResponseMessage response, int status, string state, string? code = null)
    {
        using (response)
        {
            var bytes = await response.Content.ReadAsStringAsync();
            Assert.True((int)response.StatusCode == status, $"Expected {status}, got {(int)response.StatusCode}: {bytes}");
            var body = JsonSerializer.Deserialize<JsonElement>(bytes);
            Assert.Equal(state, body.GetProperty("state").GetString());
            if (code != null) Assert.Equal(code, body.GetProperty("code").GetString());
            return body;
        }
    }

    /// <summary>No unconfigured optional contact may reach the network.</summary>
    private sealed class NoProvider : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("Unexpected external provider contact.");
    }

    /// <summary>Faults only after the real transaction commits, leaving its receipt available to a fresh request.</summary>
    private sealed class LostCommit : DbTransactionInterceptor
    {
        internal bool Armed;
        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (!Armed) return Task.CompletedTask;
            Armed = false; throw new IOException("Controlled loss after committed Location action.");
        }
    }
}
