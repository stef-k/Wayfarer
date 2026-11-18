using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Tests.Controllers;

/// <summary>Routed Group workflow evidence across the bearer and cookie surfaces on PostgreSQL.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed partial class MobileGroupWorkflowPostgresTests(PostgresImportTestFixture fixture) : TestBase
{
    /// <summary>Verifies current membership, older history and confirmation across multiple episodes.</summary>
    [PostgresFact]
    public async Task CreateInviteAcceptShareLeaveAndRejoin_UsesCurrentMembership()
    {
        await using var db = fixture.CreateContext();
        var owner = await SeedBearerAsync(db);
        var peer = await SeedBearerAsync(db);
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(),
            services => services.AddScoped(_ => fixture.CreateContext()));
        using var manager = Mobile(app.GetTestClient(), owner);
        using var recipient = Mobile(app.GetTestClient(), peer);

        var inbox = await JsonAsync(await recipient.GetAsync("/api/mobile/invitations"), HttpStatusCode.OK);
        Assert.Empty(inbox.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, inbox.GetProperty("nextCursor").ValueKind);
        var groupId = await CreateAsync(manager, "Friends");
        var group = $"/api/mobile/groups/{groupId}";
        var capture = await StoreOlderLocationAsync(db, owner);
        var userName = await db.Users.Where(u => u.Id == peer).Select(u => u.UserName).SingleAsync();
        var search = await JsonAsync(await manager.GetAsync($"{group}/invite-candidates?query={Uri.EscapeDataString(userName!)}"), HttpStatusCode.OK);
        Assert.Equal(peer, Assert.Single(search.EnumerateArray()).GetProperty("userId").GetString());

        var invitation = await InviteAsync(manager, group, peer);
        await CodeAsync(await manager.GetAsync(invitation), HttpStatusCode.NotFound, "invitation_unavailable");
        var original = await DisclosureAsync(recipient, invitation);
        Assert.Equal("Default", original.GetProperty("preferenceSource").GetString());
        Assert.True(original.GetProperty("sharesWithPeers").GetBoolean());
        await JsonAsync(await recipient.PostAsJsonAsync(invitation + "/accept", new { expected = original }), HttpStatusCode.OK);

        var membership = await SummaryAsync(recipient);
        Assert.True(membership.GetProperty("isMember").GetBoolean());
        Assert.Equal(2, membership.GetProperty("memberCount").GetInt32());
        await VerifyHistoryAsync(recipient, group, capture);
        var sharing = await JsonAsync(await recipient.GetAsync(group + "/peer-visibility"), HttpStatusCode.OK);
        var oldReference = sharing.GetProperty("membershipRef").GetString();
        await JsonAsync(await recipient.PostAsJsonAsync(group + "/peer-visibility",
            new { membershipRef = oldReference, disabled = true }), HttpStatusCode.OK);
        var leave = await LeaveReviewAsync(recipient, group);
        Assert.Equal(oldReference, leave.GetProperty("membershipRef").GetString());
        await JsonAsync(await recipient.PostAsJsonAsync(group + "/leave", new { expected = leave }), HttpStatusCode.OK);
        await CodeAsync(await recipient.GetAsync(group + "/peer-visibility"), HttpStatusCode.Forbidden, "group_role_required");
        using var denied = await recipient.PostAsJsonAsync(group + "/locations/query", new { });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var freshInvite = await InviteAsync(manager, group, peer);
        var retained = await DisclosureAsync(recipient, freshInvite);
        Assert.Equal("Retained", retained.GetProperty("preferenceSource").GetString());
        Assert.True(retained.GetProperty("disabled").GetBoolean());
        Assert.False(retained.GetProperty("sharesWithPeers").GetBoolean());
        await JsonAsync(await recipient.PostAsJsonAsync(freshInvite + "/accept", new { expected = retained }), HttpStatusCode.OK);
        await CodeAsync(await recipient.PostAsJsonAsync(group + "/leave", new { expected = leave }),
            HttpStatusCode.Conflict, "membership_changed");
        await CodeAsync(await recipient.PostAsJsonAsync(group + "/peer-visibility",
            new { membershipRef = oldReference, disabled = false }), HttpStatusCode.Conflict, "membership_changed");
        await VerifyHistoryAsync(recipient, group, capture);

        // The released empty object is a real scalar write, including after rejoin.
        var scalar = await JsonAsync(await recipient.PostAsJsonAsync(group + "/peer-visibility", new { }), HttpStatusCode.OK);
        Assert.False(scalar.GetProperty("disabled").GetBoolean());
        using var invalid = await recipient.PostAsJsonAsync(group + "/peer-visibility", new { membershipRef = (string?)null, disabled = false });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.True((await JsonAsync(await recipient.GetAsync(group + "/peer-visibility"), HttpStatusCode.OK))
            .GetProperty("sharesWithPeers").GetBoolean());
        using var scalarBadRoute = await recipient.PostAsJsonAsync("/api/mobile/groups/invalid/peer-visibility", new { });
        Assert.Equal(HttpStatusCode.NotFound, scalarBadRoute.StatusCode);
    }

    /// <summary>Uses the fixture's Identity account and persists its real token authority.</summary>
    private async Task<string> SeedBearerAsync(ApplicationDbContext db)
    {
        var user = await fixture.CreateUserAsync();
        db.Attach(user);
        db.ApiTokens.Add(new() { Name = "group-workflow", Token = user.Id, UserId = user.Id, User = user });
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>Supplies the same bearer syntax used by the mobile client.</summary>
    private static HttpClient Mobile(HttpClient client, string user)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user);
        return client;
    }

    /// <summary>Creates a Group through the routed bearer action and verifies canonical type.</summary>
    private static async Task<Guid> CreateAsync(HttpClient client, string type)
    {
        var created = await JsonAsync(await client.PostAsJsonAsync("/api/mobile/groups",
            new { name = $" Group-{Guid.NewGuid():N} ", groupType = type }), HttpStatusCode.Created);
        Assert.Equal(type, created.GetProperty("groupType").GetString());
        return created.GetProperty("groupId").GetGuid();
    }

    /// <summary>Issues to the exact target account and returns its recipient route.</summary>
    private static async Task<string> InviteAsync(HttpClient client, string group, string user) =>
        "/api/mobile/invitations/" + (await JsonAsync(await client.PostAsJsonAsync(group + "/invitations",
            new { inviteeUserId = user }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    /// <summary>Reads the reviewed values from the routed disclosure.</summary>
    private static async Task<JsonElement> DisclosureAsync(HttpClient client, string invitation) =>
        (await JsonAsync(await client.GetAsync(invitation), HttpStatusCode.OK)).GetProperty("disclosure");

    /// <summary>Reads the current Leave review from the routed preview.</summary>
    private static async Task<JsonElement> LeaveReviewAsync(HttpClient client, string group) =>
        (await JsonAsync(await client.GetAsync(group + "/leave"), HttpStatusCode.OK)).GetProperty("review");

    /// <summary>Uses the same collection read as the mobile Refresh path.</summary>
    private static async Task<JsonElement> SummaryAsync(HttpClient client) =>
        Assert.Single((await JsonAsync(await client.GetAsync("/api/mobile/groups?scope=all"), HttpStatusCode.OK)).EnumerateArray());

    /// <summary>Reads and disposes the response after reporting its full body on a contract failure.</summary>
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == status, $"Expected {status}, got {response.StatusCode}: {body}");
            using var json = JsonDocument.Parse(body);
            return json.RootElement.Clone();
        }
    }

    /// <summary>Asserts the exact routed refusal status and bounded domain code.</summary>
    private static async Task CodeAsync(HttpResponseMessage response, HttpStatusCode status, string code) =>
        Assert.Equal(code, (await JsonAsync(response, status)).GetProperty("code").GetString());

    /// <summary>Persists a real peer observation predating both membership episodes.</summary>
    private static async Task<Location> StoreOlderLocationAsync(ApplicationDbContext db, string owner)
    {
        var captured = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var row = new Location
        {
            UserId = owner, Coordinates = new Point(10.001, 10.001) { SRID = 4326 }, TimeZoneId = "UTC",
            Timestamp = captured, LocalTimestamp = captured, Country = "fixture"
        };
        db.Locations.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    /// <summary>Queries the older UTC event through the existing authorized Group history route.</summary>
    private static async Task VerifyHistoryAsync(HttpClient client, string group, Location row)
    {
        var history = await JsonAsync(await client.PostAsJsonAsync(group + "/locations/query", new
        {
            minLng = 10, minLat = 10, maxLng = 11, maxLat = 11, dateType = "day",
            year = row.Timestamp.Year, month = row.Timestamp.Month, day = row.Timestamp.Day
        }), HttpStatusCode.OK);
        Assert.Equal(row.Id, Assert.Single(history.GetProperty("results").EnumerateArray()).GetProperty("id").GetInt32());
    }
}
