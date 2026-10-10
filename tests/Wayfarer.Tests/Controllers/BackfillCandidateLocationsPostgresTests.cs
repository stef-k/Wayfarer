using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Point = NetTopologySuite.Geometries.Point;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Exercises candidate pagination through production MVC, cookie authority and PostGIS SQL.</summary>
[Collection(PostgresImportTestCollection.Name)]
public class BackfillCandidateLocationsPostgresTests(PostgresImportTestFixture fixture) : TestBase
{
    /// <summary>Normalized metadata describes the exact ordered owner rows selected, including omitted defaults.</summary>
    [PostgresTheory]
    [InlineData("&page=0&pageSize=1000", 1, 200, 0)]
    [InlineData("&page=-2&pageSize=0", 1, 1, 0)]
    [InlineData("&page=2&pageSize=50", 2, 50, 50)]
    [InlineData("", 1, 50, 0)]
    public async Task CandidateLocations_ReportsEffectiveQueryPagination(
        string paginationQuery, int expectedPage, int expectedPageSize, int expectedOffset)
    {
        var firstSeenUtc = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var lastSeenUtc = firstSeenUtc.AddMinutes(30);
        var (userId, matching) = await SeedLocationsAsync(firstSeenUtc, lastSeenUtc);
        await using var db = fixture.CreateContext();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(),
            services => services.AddScoped(_ => fixture.CreateContext()));
        using var client = app.GetTestClient();
        Cookie(client, app.Services, userId);

        // No antiforgery header is required for this authenticated GET. Only the query string binds paging.
        using var response = await client.GetAsync($"/api/backfill/candidate-locations?placeId={Guid.NewGuid()}"
            + $"&lat=37&lon=23&firstSeenUtc={firstSeenUtc:O}&lastSeenUtc={lastSeenUtc:O}&radius=500"
            + paginationQuery);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var envelope = document.RootElement;
        Assert.Equal(new[] { "data", "success" }, envelope.EnumerateObject().Select(p => p.Name).Order());
        Assert.True(envelope.GetProperty("success").GetBoolean());
        var data = envelope.GetProperty("data");
        Assert.Equal(new[] { "locations", "page", "pageSize", "totalCount" },
            data.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(3001, data.GetProperty("totalCount").GetInt32());
        var rows = data.GetProperty("locations").EnumerateArray().ToArray();
        Assert.Equal(expectedPageSize, rows.Length);
        var expected = matching.Skip(expectedOffset).Take(expectedPageSize).ToArray();
        Assert.Equal(expected.Select(l => l.Id), rows.Select(l => l.GetProperty("id").GetInt32()));
        Assert.Equal(expected.Select(l => l.LocalTimestamp),
            rows.Select(l => l.GetProperty("localTimestamp").GetDateTime()));
        Assert.All(rows, row => Assert.Equal(DateTimeKind.Utc, row.GetProperty("localTimestamp").GetDateTime().Kind));
        Assert.Equal(37, rows[0].GetProperty("latitude").GetDouble());
        Assert.Equal(23, rows[0].GetProperty("longitude").GetDouble());
        Assert.Equal(0, rows[0].GetProperty("distanceMeters").GetDouble());
        Assert.Equal(12.5, rows[0].GetProperty("accuracy").GetDouble());
        Assert.Equal(1.5, rows[0].GetProperty("speed").GetDouble());
        Assert.Equal("Fixture address", rows[0].GetProperty("address").GetString());
        Assert.False(rows[0].TryGetProperty("activity", out _));
        if (rows.Length > 1)
        {
            foreach (var field in new[] { "accuracy", "speed", "address", "activity" })
                Assert.False(rows[1].TryGetProperty(field, out _));
        }
        Assert.Equal(expectedPage, data.GetProperty("page").GetInt32());
        Assert.Equal(expectedPageSize, data.GetProperty("pageSize").GetInt32());
    }

    /// <summary>Seeds 3,001 matches across the inclusive buffered window, with foreign/spatial/time exclusions.</summary>
    private async Task<(string UserId, Location[] Matching)> SeedLocationsAsync(DateTime firstSeenUtc, DateTime lastSeenUtc)
    {
        var owner = await fixture.CreateUserAsync();
        var foreign = await fixture.CreateUserAsync();
        var start = firstSeenUtc.AddHours(-1);
        var end = lastSeenUtc.AddHours(1);
        var matching = Enumerable.Range(0, 3001).Select(index => new Location
        {
            UserId = owner.Id,
            Coordinates = new Point(23, 37) { SRID = 4326 },
            Timestamp = start.AddSeconds(index * 3),
            LocalTimestamp = start.AddSeconds(index * 3),
            TimeZoneId = "UTC",
            Accuracy = index is 0 or 50 ? 12.5 : null,
            Speed = index is 0 or 50 ? 1.5 : null,
            Address = index is 0 or 50 ? "Fixture address" : null
        }).ToArray();
        await using var db = fixture.CreateContext();
        // Reverse insertion makes timestamp ordering observable independently of generated IDs.
        db.Locations.AddRange(matching.Reverse());
        foreach (var (userId, timestamp, longitude) in new[]
        {
            (foreign.Id, start, 23d),
            (owner.Id, start.AddSeconds(-1), 23d),
            (owner.Id, end.AddSeconds(1), 23d),
            (owner.Id, start, 24d)
        })
        {
            db.Locations.Add(new Location
            {
                UserId = userId, Coordinates = new Point(longitude, 37) { SRID = 4326 },
                Timestamp = timestamp, LocalTimestamp = timestamp, TimeZoneId = "UTC"
            });
        }
        await db.SaveChangesAsync();
        // The shared fixture cleans only its registered users and their cascading locations, even on failure.
        return (owner.Id, matching);
    }

    /// <summary>Uses the existing protected Identity-cookie convention so production authentication still runs.</summary>
    private static void Cookie(HttpClient client, IServiceProvider services, string userId)
    {
        var scheme = IdentityConstants.ApplicationScheme;
        var options = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(scheme);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Name, userId),
            new Claim(ClaimTypes.Role, "User")], scheme));
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties
        {
            IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10)
        }, scheme);
        client.DefaultRequestHeaders.Add("Cookie", options.Cookie.Name + "=" + options.TicketDataFormat.Protect(ticket));
    }
}
