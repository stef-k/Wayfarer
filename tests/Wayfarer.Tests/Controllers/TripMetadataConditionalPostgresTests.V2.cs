using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using static Wayfarer.Tests.Controllers.TripMetadataReceiptHttp;

namespace Wayfarer.Tests.Controllers;

public sealed partial class TripMetadataConditionalPostgresTests
{
    /// <summary>A lost mixed receipt contains its stored comparison digests and the atomic privacy repair.</summary>
    [PostgresFact]
    public async Task V2_MixedReceiptRecoversLegacyOriginalsAndDerivedSharing()
    {
        var trip = await SeedAsync("");
        await using var db = fixture.CreateContext();
        await db.Trips.Where(t => t.Id == trip.Id).ExecuteUpdateAsync(set => set
            .SetProperty(t => t.CenterLon, (double?)null).SetProperty(t => t.Zoom, 20).SetProperty(t => t.CoverImageUrl, ""));
        await using var host = await StartAsync(db, middleware: app => app.Use(async (context, next) =>
        {
            if (context.Request.Method != "PATCH") { await next(); return; }
            var transport = context.Response.Body;
            await using var buffer = new MemoryStream();
            context.Response.Body = buffer;
            try { await next(); }
            finally { context.Response.Body = transport; }
            throw new IOException("Controlled lost mixed response");
        }));
        using var web = await WebAsync(host, trip.UserId);
        var write = TripControlWrite.Create(trip.Id, "editor/metadata/conditional", HttpMethod.Patch, "metadata", true, new
        {
            expected = new { name = trip.Name, notesHtml = "", isPublic = true, coverImage = new { rawUrl = "" },
                center = new { latitude = 1, longitude = (double?)null }, zoom = 20, contributionPolicy = (string?)null, credits = Array.Empty<object>() },
            changes = new { name = " New title ", notesHtml = "<p>New notes</p>", isPublic = false, coverImage = (object?)null,
                center = (object?)null, zoom = 0, contributionPolicy = "view-only", credits = new[] { new { contributor = "Author" } } }
        });
        var permit = await write.PermitAsync(web, trip);
        using var lost = await write.DispatchAsync(web, permit);
        Assert.Equal(HttpStatusCode.InternalServerError, lost.StatusCode);
        using var lookup = await write.LookupAsync(web, permit);
        var receipt = await EvidenceAsync(lookup, permit, "applied");
        var digests = receipt.GetProperty("fieldDigests");
        Assert.Equal(8, digests.EnumerateObject().Count());
        Assert.Equal("sha256:2a71aaa7f0a7770d289e18db81dbbe45ae2ed3f5d6bf42d105314e6b94c7caa4", digests.GetProperty("center").GetString());
        Assert.True(receipt.GetProperty("privacyEffect").GetProperty("disabledShareProgress").GetBoolean());
        var saved = await db.Trips.AsNoTracking().SingleAsync(t => t.Id == trip.Id);
        Assert.Equal(("New title", "<p>New notes</p>", false, false, 0), (saved.Name, saved.Notes, saved.IsPublic, saved.ShareProgressEnabled, saved.Zoom));
        Assert.Equal((null, null, null), (saved.CenterLat, saved.CenterLon, saved.CoverImageUrl));
        output.WriteLine(receipt.GetRawText());
    }

    /// <summary>Publish-only compares the exact reviewed policy even though it writes no policy field.</summary>
    [PostgresFact]
    public async Task V2_PublishOnlyRefusesAnInterveningBearerPolicyChange()
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        await db.Trips.Where(t => t.Id == trip.Id).ExecuteUpdateAsync(set => set.SetProperty(t => t.IsPublic, false)
            .SetProperty(t => t.ShareProgressEnabled, false).SetProperty(t => t.ContributionPolicy, "CC-BY-4.0"));
        await using var host = await StartAsync(db);
        using var web = await WebAsync(host, trip.UserId);
        using var bearer = Bearer(host, trip.UserId);
        var publish = TripControlWrite.Create(trip.Id, "editor/metadata/conditional", HttpMethod.Patch, "metadata", true,
            new { expected = new { isPublic = false, contributionPolicy = "CC-BY-4.0" }, changes = new { isPublic = true } });
        var permit = await publish.PermitAsync(web, trip);
        using var policy = await PatchAsync(bearer, trip.Id, false, new { contributionPolicy = "CC-BY-4.0" }, new { contributionPolicy = "view-only" });
        policy.EnsureSuccessStatusCode();
        using var response = await publish.DispatchAsync(web, permit);
        var receipt = await EvidenceAsync(response, permit, "terminalUnapplied", 409);
        Assert.Equal("mutation-base-stale", receipt.GetProperty("code").GetString());
        Assert.False(await db.Trips.Where(t => t.Id == trip.Id).Select(t => t.IsPublic).SingleAsync());
        var reviewed = publish with { Body = publish.Body.Replace("CC-BY-4.0", "view-only", StringComparison.Ordinal) };
        var reviewedPermit = await reviewed.PermitAsync(web, trip);
        using var recorded = await reviewed.DispatchAsync(web, reviewedPermit);
        var accepted = await EvidenceAsync(recorded, reviewedPermit, "applied");
        Assert.Equal("isPublic", Assert.Single(accepted.GetProperty("fieldDigests").EnumerateObject()).Name);
    }

    /// <summary>Already-open cookie pages receive a bounded refusal without creating effects or history.</summary>
    [PostgresFact]
    public async Task V2_SupersededCookieWritersRefuseWithZeroEffects()
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var host = await StartAsync(db);
        using var web = await WebAsync(host, trip.UserId);
        var editor = $"/api/trips/{trip.Id}/editor";
        var user = $"/User/Trip/{trip.Id}/Tags";
        using var metadata = await web.PatchAsJsonAsync(editor + "/metadata/conditional", new { expected = new { name = trip.Name }, changes = new { name = "Old tab" } });
        using var replace = await web.PutAsJsonAsync(editor + "/tags", new { tags = new[] { "Old tab" } });
        using var sharing = await web.PatchAsJsonAsync(editor + "/share-progress", new { enabled = false });
        using var attach = await web.PostAsJsonAsync(user, new { tags = new[] { "Old tab" } });
        using var detach = await web.DeleteAsync(user + "/old-tab");
        using var viewer = await web.PostAsync($"/User/Trip/ToggleShareProgress?id={trip.Id}&enabled=false", null);
        foreach (var reply in new[] { metadata, replace, sharing, attach, detach, viewer })
        {
            Assert.Equal(HttpStatusCode.Conflict, reply.StatusCode);
            var refusal = await reply.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("trip-write-upgrade-required", refusal.GetProperty("code").GetString());
            Assert.False(refusal.TryGetProperty("state", out _));
        }
        var saved = await db.Trips.AsNoTracking().Include(t => t.Tags).SingleAsync(t => t.Id == trip.Id);
        Assert.Equal((trip.Name, trip.ShareProgressEnabled, trip.UpdatedAt), (saved.Name, saved.ShareProgressEnabled, saved.UpdatedAt));
        Assert.Empty(saved.Tags);
    }
}
