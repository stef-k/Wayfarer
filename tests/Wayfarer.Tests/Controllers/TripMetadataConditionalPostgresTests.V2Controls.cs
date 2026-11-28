using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using static Wayfarer.Tests.Controllers.TripMetadataReceiptHttp;

namespace Wayfarer.Tests.Controllers;

public sealed partial class TripMetadataConditionalPostgresTests
{
    /// <summary>Historical tag effects retain the original exact joins and catalogue deltas through later changes.</summary>
    [PostgresFact]
    public async Task V2_TagsRecordExactHistoricalEffectsAndRejectARecreatedOriginal()
    {
        var owner = await SeedAsync();
        var another = await SeedAsync();
        await using var db = fixture.CreateContext();
        var privateTag = NewTag();
        var sharedTag = NewTag();
        db.Trips.AttachRange(owner, another);
        owner.Tags.Add(privateTag);
        owner.Tags.Add(sharedTag);
        another.Tags.Add(sharedTag);
        db.Tags.AddRange(privateTag, sharedTag);
        await db.SaveChangesAsync();
        var names = NewTag();
        await using var host = await StartAsync(db);
        using var web = await WebAsync(host, owner.UserId);
        using var bearer = Bearer(host, owner.UserId);
        var replace = TripControlWrite.Create(owner.Id, "editor/tags/conditional", HttpMethod.Put, "tags-replace", true, new { tags = new[] { names.Name } });
        var permit = await replace.PermitAsync(web, owner);
        using var dispatched = await replace.DispatchAsync(web, permit);
        var recorded = await EvidenceAsync(dispatched, permit, "applied");
        var result = recorded.GetProperty("tagResult");
        var created = Assert.Single(result.GetProperty("createdCatalogue").EnumerateArray());
        var createdId = created.GetProperty("id").GetGuid();
        fixture.RegisterTag(new() { Id = createdId });
        Assert.Equal(createdId, Assert.Single(result.GetProperty("addedJoins").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(createdId, Assert.Single(result.GetProperty("resultingTags").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(2, result.GetProperty("removedJoins").GetArrayLength());
        Assert.Equal(privateTag.Id, Assert.Single(result.GetProperty("deletedCatalogue").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.False(recorded.TryGetProperty("updatedAtUtc", out _));
        Assert.True(await db.Trips.AsNoTracking().AnyAsync(t => t.Id == another.Id && t.Tags.Any(tag => tag.Id == sharedTag.Id)));

        var clear = TripControlWrite.Create(owner.Id, $"/User/Trip/{owner.Id}/Tags/detach/conditional", HttpMethod.Post, "tags-detach",
            true, new { slug = names.Slug });
        var clearPermit = await clear.PermitAsync(web, owner);
        using var detached = await clear.DispatchAsync(web, clearPermit);
        await EvidenceAsync(detached, clearPermit, "applied");
        var attach = TripControlWrite.Create(owner.Id, $"/User/Trip/{owner.Id}/Tags/attach/conditional", HttpMethod.Post, "tags-attach",
            true, new { tags = new[] { "", names.Name } });
        using var attached = await attach.DispatchAsync(web, await attach.PermitAsync(web, owner));
        attached.EnsureSuccessStatusCode();
        var currentId = (await db.Trips.AsNoTracking().Include(t => t.Tags).SingleAsync(t => t.Id == owner.Id)).Tags.Single().Id;
        fixture.RegisterTag(new() { Id = currentId });
        Assert.NotEqual(createdId, currentId);
        var bearerReplace = TripControlWrite.Create(owner.Id, "conditional/tags", HttpMethod.Put, "tags-replace", false,
            new { expected = new[] { new { id = createdId, slug = names.Slug } }, tags = Array.Empty<string>() });
        var stalePermit = await bearerReplace.PermitAsync(bearer, owner);
        using var stale = await bearerReplace.DispatchAsync(bearer, stalePermit);
        Assert.Equal("mutation-base-stale", (await EvidenceAsync(stale, stalePermit, "terminalUnapplied", 409)).GetProperty("code").GetString());
        using var lookup = await replace.LookupAsync(web, permit);
        using var replay = await replace.DispatchAsync(web, permit);
        Assert.Equal(recorded.ToString(), (await EvidenceAsync(lookup, permit, "applied")).ToString());
        Assert.Equal(recorded.ToString(), (await EvidenceAsync(replay, permit, "applied")).ToString());
        Assert.True(await db.Trips.AsNoTracking().AnyAsync(t => t.Id == owner.Id && t.UpdatedAt == owner.UpdatedAt
            && t.Tags.Any(tag => tag.Id == currentId)));
        output.WriteLine(recorded.GetRawText());
    }

    /// <summary>Resets legacy partial coordinates and zoom 20, then exercises complete Set and credential boundaries.</summary>
    [PostgresFact]
    public async Task V2_BearerSavedViewUsesTheCompleteTupleAndOwnerCredential()
    {
        var trip = await SeedAsync();
        var foreign = await SeedAsync();
        await using var db = fixture.CreateContext();
        await db.Trips.Where(t => t.Id == trip.Id).ExecuteUpdateAsync(set => set.SetProperty(t => t.CenterLon, (double?)null)
            .SetProperty(t => t.Zoom, 20));
        await using var host = await StartAsync(db);
        using var bearer = Bearer(host, trip.UserId);
        using var nonOwner = Bearer(host, foreign.UserId);
        var reset = SavedView(trip, new { center = new { latitude = 1, longitude = (double?)null }, zoom = 20 }, new { mode = "reset" });
        using var declined = await reset.IssueAsync(nonOwner, foreign.UserId, trip.Id);
        Assert.Equal(HttpStatusCode.NotFound, declined.StatusCode);
        var resetPermit = await reset.PermitAsync(bearer, trip);
        using var response = await reset.DispatchAsync(bearer, resetPermit);
        var receipt = await EvidenceAsync(response, resetPermit, "applied");
        var saved = receipt.GetProperty("savedView");
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("zoom").ValueKind);
        Assert.All(saved.GetProperty("center").EnumerateObject(), coordinate => Assert.Equal(JsonValueKind.Null, coordinate.Value.ValueKind));
        var current = new { center = new { latitude = (double?)null, longitude = (double?)null }, zoom = (int?)null };
        var minimum = SavedView(trip, current, new { mode = "set", center = new { latitude = -90, longitude = 180 }, zoom = 0 });
        using var minResponse = await minimum.DispatchAsync(bearer, await minimum.PermitAsync(bearer, trip));
        minResponse.EnsureSuccessStatusCode();
        var stale = SavedView(trip, current, new { mode = "reset" });
        var stalePermit = await stale.PermitAsync(bearer, trip);
        using var staleResult = await stale.DispatchAsync(bearer, stalePermit);
        await EvidenceAsync(staleResult, stalePermit, "terminalUnapplied", 409);
        var next = new { center = new { latitude = -90, longitude = 180 }, zoom = 0 };
        var maximum = SavedView(trip, next, new { mode = "set", center = new { latitude = 90, longitude = -180 }, zoom = 19 });
        using var maxResponse = await maximum.DispatchAsync(bearer, await maximum.PermitAsync(bearer, trip));
        maxResponse.EnsureSuccessStatusCode();

        foreach (var invalid in new[] {
            SavedView(trip, next, new { mode = "set", center = new { latitude = 0, longitude = 0 }, zoom = 20 }),
            SavedView(trip, next, new { mode = "reset", isPublic = true }),
            TripControlWrite.Create(trip.Id, "conditional/tags", HttpMethod.Put, "tags-replace", false,
                new { expected = Array.Empty<object>(), tags = Array.Empty<string>(), enabled = false })
        })
        {
            using var refused = await invalid.DispatchAsync(bearer, await invalid.PermitAsync(bearer, trip));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }
        var revoked = await reset.PermitAsync(bearer, trip);
        await db.ApiTokens.Where(t => t.UserId == trip.UserId).ExecuteDeleteAsync();
        using var revokedReply = await reset.DispatchAsync(bearer, revoked);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedReply.StatusCode);
        Assert.True(await db.Trips.AnyAsync(t => t.Id == trip.Id && t.CenterLat == 90 && t.CenterLon == -180 && t.Zoom == 19));
        output.WriteLine(receipt.GetRawText());
        output.WriteLine(await maxResponse.Content.ReadAsStringAsync());
    }

    /// <summary>Builds one exact bearer saved-view body.</summary>
    private static TripControlWrite SavedView(Trip trip, object expected, object value) =>
        TripControlWrite.Create(trip.Id, "conditional/saved-view", HttpMethod.Put, "saved-view", false, new { expected, value });

    /// <summary>Generates a fixture-scoped identity for the global catalogue.</summary>
    private Tag NewTag()
    {
        var slug = "test" + Guid.NewGuid().ToString("N");
        var tag = new Tag { Id = Guid.NewGuid(), Name = slug, Slug = slug };
        fixture.RegisterTag(tag);
        return tag;
    }
}
