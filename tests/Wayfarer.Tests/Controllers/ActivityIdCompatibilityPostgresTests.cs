using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Tests.Mocks;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Tests.Models;
using Xunit;
using OwnerLocationController = Wayfarer.Areas.User.Controllers.LocationController;

namespace Wayfarer.Tests.Controllers;

/// <summary>Unchanged queued Mobile IDs and stale owner forms reach actual controllers after real cleanup.</summary>
[Collection(PostgresMigrationTestCollection.Name)]
public sealed class ActivityIdCompatibilityPostgresTests(PostgresMigrationTestFixture fixture) : TestBase
{
    /// <summary>Catalog refresh cannot retire queued IDs; API writes/replays and owner forms keep survivor FKs.</summary>
    [PostgresFact]
    public async Task RoutedStaleIdsPreservePublishedWriterContracts()
    {
        var owner = await fixture.CreateUserAsync();
        var other = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        try
        {
            db.ApiTokens.AddRange(new ApiToken { UserId = owner.Id, User = null!, Name = "phone", Token = owner.Id },
                new ApiToken { UserId = other.Id, User = null!, Name = "phone", Token = other.Id });
            await db.SaveChangesAsync();
            await db.GetService<IMigrator>().MigrateAsync(ActivityCatalogMigrationPostgresTests.Previous);
            await ActivityCatalogMigrationPostgresTests.SeedLegacyAsync(db);
            await db.Database.MigrateAsync();
            await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
            {
                services.AddControllers().AddControllersAsServices();
                services.AddTransient(_ => ApiLocationAdmissionTests.Controller(db, new ApiWorkAdmission(64, 8)));
                services.AddTransient(_ => new OwnerLocationController(NullLogger<BaseController>.Instance, db,
                    new ReverseGeocodingService(new HttpClient(), NullLogger<Wayfarer.Areas.Api.Controllers.BaseApiController>.Instance),
                    new SseService(), new NullPlaceVisitDetectionService()));
            });
            using var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Id);
            using (var refresh = await client.GetAsync("/api/activity"))
            {
                refresh.EnsureSuccessStatusCode();
                var ids = (await refresh.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Select(a => a.GetProperty("id").GetInt32());
                Assert.DoesNotContain(102, ids);
            }
            var logKey = Guid.NewGuid();
            var dto = ApiLocationAdmissionTests.Dto(); dto.ActivityTypeId = 102;
            client.DefaultRequestHeaders.Add("Idempotency-Key", logKey.ToString());
            using var logged = await client.PostAsJsonAsync("/api/location/log-location", dto);
            logged.EnsureSuccessStatusCode();
            var logBody = await logged.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(logBody.GetProperty("success").GetBoolean());
            Assert.False(logBody.GetProperty("skipped").GetBoolean());
            var logId = logBody.GetProperty("locationId").GetInt32();
            Assert.Equal(101, await StoredActivityAsync(logId));

            var checkKey = Guid.NewGuid();
            client.DefaultRequestHeaders.Remove("Idempotency-Key");
            client.DefaultRequestHeaders.Add("Idempotency-Key", checkKey.ToString());
            using var check = await client.PostAsJsonAsync("/api/location/check-in", dto);
            check.EnsureSuccessStatusCode();
            Assert.Contains("Check-in logged successfully", await check.Content.ReadAsStringAsync());
            var checkId = await db.Locations.Where(l => l.IdempotencyKey == checkKey).Select(l => l.Id).SingleAsync();
            Assert.Equal(101, await StoredActivityAsync(checkId));
            dto.ActivityTypeId = int.MaxValue; // Persisted success is returned before new-write validation.
            using var replay = await client.PostAsJsonAsync("/api/location/check-in", dto);
            replay.EnsureSuccessStatusCode();
            client.DefaultRequestHeaders.Remove("Idempotency-Key");
            client.DefaultRequestHeaders.Add("Idempotency-Key", logKey.ToString());
            using var logReplay = await client.PostAsJsonAsync("/api/location/log-location", dto);
            logReplay.EnsureSuccessStatusCode();
            Assert.Equal(logId, (await logReplay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("locationId").GetInt32());
            Assert.Equal(2, await db.Locations.CountAsync());
            Assert.Equal(101, await StoredActivityAsync(checkId));
            client.DefaultRequestHeaders.Remove("Idempotency-Key");
            foreach (var (payload, expected) in new (LocationUpdateRequestDto, int?)[]
            {
                (new() { ActivityTypeId = 102 }, 101), (new(), 101),
                (new() { ActivityTypeId = 101 }, 101),
                (new() { ActivityTypeId = -1, ActivityName = "\u00a0ΠΕΖΟΠΟΡΙ\u0301Α " }, 104),
                (new() { ActivityTypeId = int.MaxValue }, null), (new() { ActivityTypeId = -1 }, null),
                (new() { ActivityTypeId = 102, ClearActivity = true }, null),
                (new(), null), (new() { ActivityTypeId = 102 }, 101)
            })
            {
                using var put = await client.PutAsJsonAsync($"/api/location/{checkId}", payload);
                put.EnsureSuccessStatusCode();
                Assert.Contains("success", await put.Content.ReadAsStringAsync());
                Assert.Equal(expected, await StoredActivityAsync(checkId));
            }
            client.DefaultRequestHeaders.Authorization = new("Bearer", other.Id);
            using var forbidden = await client.PutAsJsonAsync($"/api/location/{checkId}", new LocationUpdateRequestDto { ClearActivity = true });
            Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
            Assert.Equal(101, await StoredActivityAsync(checkId));

            client.DefaultRequestHeaders.Authorization = null;
            var authCookie = Cookie(app.Services, owner.Id);
            client.DefaultRequestHeaders.Add("Cookie", authCookie);
            var antiforgery = await IdentityRouteHost.AntiforgeryAsync(client);
            var antiforgeryCookie = client.DefaultRequestHeaders.GetValues("Cookie").Single();
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Add("Cookie", authCookie + "; " + antiforgeryCookie);
            using var cookiePut = new HttpRequestMessage(HttpMethod.Put, $"/api/location/{checkId}")
            { Content = JsonContent.Create(new LocationUpdateRequestDto { ActivityTypeId = 102 }) };
            cookiePut.Headers.Add("RequestVerificationToken", antiforgery);
            using var cookieResult = await client.SendAsync(cookiePut);
            cookieResult.EnsureSuccessStatusCode();
            Assert.Equal(101, await StoredActivityAsync(checkId));
            using var noCsrf = await client.PutAsJsonAsync($"/api/location/{checkId}", new LocationUpdateRequestDto { ClearActivity = true });
            Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
            var fields = new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = antiforgery, ["Latitude"] = "10", ["Longitude"] = "20",
                ["SelectedActivityId"] = "102", ["LocalTimestamp"] = "2026-10-13T12:00:00", ["Notes"] = "Stale form"
            };
            using var form = await client.PostAsync("/User/Location/Create", new FormUrlEncodedContent(fields));
            Assert.Equal(HttpStatusCode.Redirect, form.StatusCode);
            var webId = await db.Locations.Where(l => l.Notes == "Stale form").Select(l => l.Id).SingleAsync();
            Assert.Equal(101, await StoredActivityAsync(webId));
            fields["Id"] = webId.ToString();
            using var edit = await client.PostAsync("/User/Location/Edit", new FormUrlEncodedContent(fields));
            Assert.Equal(HttpStatusCode.Redirect, edit.StatusCode);
            Assert.Equal(101, await StoredActivityAsync(webId));
            Assert.Equal(101, await new ActivityCatalog(db).ResolveIdAsync(102));
        }
        finally { await ActivityCatalogMigrationPostgresTests.CleanAsync(db, owner.Id, other.Id); }
    }

    /// <summary>Existing create paths keep null support and unknown/negative-ID FK failure responses.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateIdControls(bool checkIn)
    {
        var owner = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        try
        {
            db.ApiTokens.Add(new ApiToken { UserId = owner.Id, User = null!, Name = "phone", Token = owner.Id });
            await db.SaveChangesAsync();
            foreach (var id in new int?[] { -1, int.MaxValue, null })
            {
                var controller = ApiLocationAdmissionTests.Controller(db, new ApiWorkAdmission(64, 8));
                controller.Request.Headers.Authorization = "Bearer " + owner.Id;
                var dto = ApiLocationAdmissionTests.Dto(); dto.ActivityTypeId = id;
                var result = checkIn ? await controller.CheckIn(dto) : await controller.LogLocation(dto);
                Assert.Equal(id == null ? 200 : 500, Assert.IsAssignableFrom<Microsoft.AspNetCore.Mvc.ObjectResult>(result).StatusCode);
                db.ChangeTracker.Clear();
            }
            Assert.Null(await db.Locations.Select(l => l.ActivityTypeId).SingleAsync());
        }
        finally { await ActivityCatalogMigrationPostgresTests.CleanAsync(db, owner.Id); }
    }

    /// <summary>Verifies the committed FK in a fresh context, avoiding tracking-fixup evidence.</summary>
    private async Task<int?> StoredActivityAsync(int locationId)
    {
        await using var verify = fixture.CreateContext();
        return await verify.Locations.Where(l => l.Id == locationId).Select(l => l.ActivityTypeId).SingleAsync();
    }

    /// <summary>Builds a genuine Identity cookie with the existing production ticket format.</summary>
    private static string Cookie(IServiceProvider services, string userId)
    {
        return IdentityRouteHost.Cookie(services, userId);
    }
}
