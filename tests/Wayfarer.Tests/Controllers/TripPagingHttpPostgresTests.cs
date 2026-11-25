using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Verifies the bearer My Trips and anonymous Public Trips paging contracts on PostgreSQL.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class TripPagingHttpPostgresTests(PostgresImportTestFixture fixture) : TestBase
{
    /// <summary>Updated Trips page by ID within ties while owner, visibility and literal search still govern counts.</summary>
    [PostgresFact]
    public async Task MyTripsSearchPagesUpdatedTiesAfterOwnerVisibilityAndLiteralSearch()
    {
        var owner = await fixture.CreateUserAsync();
        var foreign = await fixture.CreateUserAsync();
        var marker = $"paging-1011-{Guid.NewGuid():N}";
        var query = $@"{marker} %_\end";
        var ids = NewOrderedIds();
        var firstTie = TripWithName(ids[0], owner.Id, query);
        var secondTie = TripWithName(ids[1], owner.Id, "paging-1011 notes-only match");
        secondTie.Notes = $"<p>{query}</p>";
        var thirdTie = TripWithName(ids[2], owner.Id, query);
        var newest = TripWithName(ids[3], owner.Id, query);

        await using var db = fixture.CreateContext();
        db.ApiTokens.Add(new ApiToken
        {
            UserId = owner.Id,
            User = await db.Users.SingleAsync(user => user.Id == owner.Id),
            Name = "paging-1011",
            TokenHash = ApiTokenService.HashToken(owner.Id)
        });
        await SaveTripsAsync(db, newest.Id, newest, thirdTie, secondTie, firstTie,
            TripWithName(Guid.NewGuid(), owner.Id, query, isPublic: true),
            TripWithName(Guid.NewGuid(), foreign.Id, query),
            TripWithName(Guid.NewGuid(), owner.Id, $@"{marker} wildcard_\end"),
            TripWithName(Guid.NewGuid(), owner.Id, $@"{marker} %x\end"),
            TripWithName(Guid.NewGuid(), owner.Id, $"{marker} %_end"));

        await using var app = await StartAsync(db);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Id);
        await AssertPagesAsync(client, $"/api/trips/search?visibility=private&q={Uri.EscapeDataString(query)}",
            "data", [newest.Id, firstTie.Id, secondTie.Id, thirdTie.Id], body =>
            {
                Assert.True(body.GetProperty("success").GetBoolean());
                Assert.Equal(4, body.GetProperty("totalItems").GetInt32());
                Assert.Equal(5, body.GetProperty("totalAll").GetInt32());
                Assert.Equal(1, body.GetProperty("publicCount").GetInt32());
                Assert.Equal(4, body.GetProperty("privateCount").GetInt32());
                Assert.Equal(8, body.GetProperty("userTripCount").GetInt32());
            });
    }

    /// <summary>Public date and name ordering both break ties by ID after visibility, search and tag filtering.</summary>
    [PostgresTheory]
    [InlineData(null)]
    [InlineData("updated")]
    [InlineData("name")]
    public async Task PublicTripsPageTiesAfterVisibilitySearchAndTagFilters(string? sort)
    {
        var owner = await fixture.CreateUserAsync();
        var marker = $"paging-1011-{Guid.NewGuid():N}";
        var tag = new Tag { Id = Guid.NewGuid(), Name = marker, Slug = marker };
        fixture.RegisterTag(tag);
        var ids = NewOrderedIds();
        var firstTie = TripWithName(ids[0], owner.Id, $"{marker} B", isPublic: true);
        var newest = TripWithName(ids[1], owner.Id, $"{marker} B", isPublic: true);
        var thirdTie = TripWithName(ids[2], owner.Id, $"{marker} B", isPublic: true);
        var earliestName = TripWithName(ids[3], owner.Id, $"{marker} A", isPublic: true);
        var tagged = new[]
        {
            earliestName, thirdTie, newest, firstTie,
            TripWithName(Guid.NewGuid(), owner.Id, $"{marker} B"),
            TripWithName(Guid.NewGuid(), owner.Id, "paging-1011 tag-only control", isPublic: true)
        };
        foreach (var trip in tagged)
            trip.Tags.Add(tag);

        await using var db = fixture.CreateContext();
        var untagged = TripWithName(Guid.NewGuid(), owner.Id, $"{marker} B", isPublic: true);
        await SaveTripsAsync(db, newest.Id, [.. tagged, untagged]);
        await using var app = await StartAsync(db);
        using var client = app.GetTestClient();
        var route = $"/api/trips/public?q={marker}&tags={tag.Slug}";
        if (sort is not null)
            route += $"&sort={sort}";

        Guid[] expected = sort == "name"
            ? [earliestName.Id, firstTie.Id, newest.Id, thirdTie.Id]
            : [newest.Id, firstTie.Id, thirdTie.Id, earliestName.Id];
        await AssertPagesAsync(client, route, "items", expected, body =>
        {
            Assert.Equal(4, body.GetProperty("totalCount").GetInt32());
            Assert.Equal(2, body.GetProperty("totalPages").GetInt32());
        });
    }

    /// <summary>Uses the existing routed host with a fresh relational context for each request.</summary>
    private Task<WebApplication> StartAsync(ApplicationDbContext db) =>
        IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ => fixture.CreateContext());
            services.AddSingleton(Mock.Of<ICacheWarmupScheduler>());
        });

    /// <summary>Saves the ties in reverse ID order and pins timestamps after the normal save stamps them.</summary>
    private async Task SaveTripsAsync(ApplicationDbContext db, Guid newestId, params Trip[] trips)
    {
        foreach (var trip in trips)
        {
            fixture.RegisterTrip(trip.Id);
            db.Trips.Add(trip);
            await db.SaveChangesAsync();
        }

        var ids = trips.Select(trip => trip.Id).ToArray();
        var tiedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newestAt = tiedAt.AddDays(1);
        await db.Trips.Where(trip => ids.Contains(trip.Id))
            .ExecuteUpdateAsync(set => set.SetProperty(trip => trip.UpdatedAt,
                trip => trip.Id == newestId ? newestAt : tiedAt));
    }

    /// <summary>Reads the same two disjoint pages twice and checks every position and the page envelope.</summary>
    private static async Task AssertPagesAsync(
        HttpClient client, string route, string itemsProperty, Guid[] expected, Action<JsonElement> assertCounts)
    {
        const int pageSize = 2;
        for (var read = 0; read < 2; read++)
        {
            for (var page = 1; page <= 2; page++)
            {
                var body = await client.GetFromJsonAsync<JsonElement>($"{route}&page={page}&pageSize={pageSize}");
                Assert.Equal(page, body.GetProperty("page").GetInt32());
                Assert.Equal(pageSize, body.GetProperty("pageSize").GetInt32());
                assertCounts(body);
                var actual = body.GetProperty(itemsProperty).EnumerateArray()
                    .Select(item => item.GetProperty("id").GetGuid()).ToArray();
                Assert.Equal(expected.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), actual);
            }
        }
    }

    /// <summary>Allocates unique IDs whose ascending order can be assigned independently of insertion and primary sort.</summary>
    private static Guid[] NewOrderedIds() =>
        Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).Order().ToArray();

    /// <summary>Creates a catalogue row with the requested visibility and owner.</summary>
    private static Trip TripWithName(Guid id, string ownerId, string name, bool isPublic = false) =>
        new() { Id = id, UserId = ownerId, Name = name, IsPublic = isPublic };
}
