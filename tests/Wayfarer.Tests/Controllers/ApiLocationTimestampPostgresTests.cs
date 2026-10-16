using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;
using Xunit.Abstractions;

namespace Wayfarer.Tests.Controllers;

/// <summary>Proves timestamp interpretation through authenticated MVC JSON binding and PostgreSQL reloads.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class ApiLocationTimestampPostgresTests(PostgresImportTestFixture fixture, ITestOutputHelper output)
{
    /// <summary>Equivalent offset/Z captures persist the same instant; unspecified clocks retain coordinate time.</summary>
    [PostgresTheory]
    [InlineData("check-in")]
    [InlineData("log-location")]
    public async Task CaptureTimestamp_PreservesBoundInstantAndLegacyWallTime(string route)
    {
        foreach (var sample in new[]
        {
            (Text: "2026-08-22T10:00:00+02:00", Kind: DateTimeKind.Local, Hour: 8),
            (Text: "2026-08-22T08:00:00Z", Kind: DateTimeKind.Utc, Hour: 8),
            (Text: "2026-08-22T03:00:00-05:00", Kind: DateTimeKind.Local, Hour: 8),
            (Text: "2026-08-22T10:00:00", Kind: DateTimeKind.Unspecified, Hour: 7),
            (Text: "2026-03-29T03:30:00", Kind: DateTimeKind.Unspecified, Hour: -1),
            (Text: "2026-10-25T03:30:00", Kind: DateTimeKind.Unspecified, Hour: -1)
        })
        {
            // Each automatic sample gets its own owner and cache so filters cannot mask an insert.
            var user = await fixture.CreateUserAsync();
            var key = Guid.NewGuid();
            var token = new ApiTokenService(null!, null!).GenerateToken();
            await using var db = fixture.CreateContext();
            db.ApiTokens.Add(new ApiToken { User = null!, UserId = user.Id, Name = "timestamp-test",
                TokenHash = ApiTokenService.HashToken(token) });
            await db.SaveChangesAsync();
            var observed = new BoundTimestamp();
            var root = Path.Combine(Path.GetTempPath(), $"wayfarer-819-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var controller = ApiLocationAdmissionTests.Controller(db, new ApiWorkAdmission(64, 8));
                await using var app = await IdentityRouteHost.StartAsync(db, root, services =>
                {
                    services.AddSingleton(controller);
                    services.AddControllers(options => options.Filters.Add(observed)).AddControllersAsServices();
                });
                using var client = app.GetTestClient();
                client.BaseAddress = new Uri("https://wayfarer.test");
                client.DefaultRequestHeaders.Authorization = new("Bearer", token);
                client.DefaultRequestHeaders.Add("Idempotency-Key", key.ToString("D"));
                var body = $$"""
                    { "latitude": 37.98, "longitude": 23.73, "accuracy": 5, "timestamp": "{{sample.Text}}" }
                    """;
                var before = DateTime.UtcNow;
                using var response = await client.PostAsync($"/api/location/{route}",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                var after = DateTime.UtcNow;
                Assert.Equal(sample.Kind, observed.Value!.Value.Kind);
                if (sample.Hour == -1)
                {
                    // Athens spring gaps and autumn overlaps retain the existing strict-conversion failure.
                    Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
                    Assert.False(await db.Locations.AnyAsync(row => row.UserId == user.Id));
                    continue;
                }
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var expected = new DateTime(2026, 8, 22, sample.Hour, 0, 0, DateTimeKind.Utc);
                if (sample.Kind != DateTimeKind.Unspecified)
                    Assert.Equal(expected, observed.Value.Value.ToUniversalTime());

                await using var verify = fixture.CreateContext();
                var saved = Assert.Single(await verify.Locations.AsNoTracking()
                    .Where(row => row.UserId == user.Id).ToListAsync());
                output.WriteLine("{0}: {1}, MVC Kind={2}, stored={3:O}, host={4}",
                    route, sample.Text, observed.Value.Value.Kind, saved.LocalTimestamp, TimeZoneInfo.Local.Id);
                Assert.Equal(expected, saved.LocalTimestamp);
                Assert.Equal(DateTimeKind.Utc, saved.LocalTimestamp.Kind);
                Assert.Equal(key, saved.IdempotencyKey);
                Assert.Equal("Europe/Athens", saved.TimeZoneId);
                Assert.InRange(saved.Timestamp, before.AddMilliseconds(-1), after.AddMilliseconds(1));
                Assert.Equal(DateTimeKind.Utc, saved.Timestamp.Kind);
                using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                AssertResponse(result.RootElement, route, saved.Id);

                // Replay acknowledges the same row without replacing its capture or receipt time.
                using var replay = await client.PostAsync($"/api/location/{route}",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
                using var replayResult = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
                AssertResponse(replayResult.RootElement, route, saved.Id);
                Assert.Equal(1, await verify.Locations.CountAsync(row => row.UserId == user.Id));
                await verify.Entry(saved).ReloadAsync();
                Assert.Equal(expected, saved.LocalTimestamp);
                Assert.InRange(saved.Timestamp, before.AddMilliseconds(-1), after.AddMilliseconds(1));
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    /// <summary>Checks the existing endpoint-specific response field names and saved identity.</summary>
    private static void AssertResponse(JsonElement result, string route, int id)
    {
        if (route == "check-in")
        {
            Assert.Equal(new[] { "message", "location" }, result.EnumerateObject().Select(item => item.Name));
            Assert.Equal("Check-in logged successfully", result.GetProperty("message").GetString());
            Assert.Equal(id, result.GetProperty("location").GetProperty("id").GetInt32());
        }
        else
        {
            Assert.Equal(new[] { "success", "skipped", "locationId" }, result.EnumerateObject().Select(item => item.Name));
            Assert.True(result.GetProperty("success").GetBoolean());
            Assert.False(result.GetProperty("skipped").GetBoolean());
            Assert.Equal(id, result.GetProperty("locationId").GetInt32());
        }
    }

    /// <summary>Observes the actual bound production DTO immediately before the action, without replacing binding.</summary>
    private sealed class BoundTimestamp : IActionFilter
    {
        /// <summary>The capture representation supplied by the real MVC JSON formatter.</summary>
        public DateTime? Value { get; private set; }
        /// <summary>Records the action argument while leaving the request unchanged.</summary>
        public void OnActionExecuting(ActionExecutingContext context) =>
            Value = context.ActionArguments.Values.OfType<GpsLoggerLocationDto>().Single().Timestamp;
        /// <summary>No post-action intervention is needed.</summary>
        public void OnActionExecuted(ActionExecutedContext context) { }
    }
}
