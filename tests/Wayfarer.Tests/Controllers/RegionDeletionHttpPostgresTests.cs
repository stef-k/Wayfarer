using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Builder;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Microsoft.Extensions.Logging;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;
using Wayfarer.Services.TripMetadataReceipts;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Tests.Models;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Routes real cookie/antiforgery and bearer requests through production MVC against PostgreSQL.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class RegionDeletionHttpPostgresTests(PostgresImportTestFixture fixture) : TestBase
{
    /// <summary>Both adapters commit their reviewed choices after a preview that makes no writes.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothAdapters_NontrivialConfirmedOperation(bool cookie)
    {
        var g = await RegionDeletionTestGraph.SeedAsync(fixture, false);
        await using var db = fixture.CreateContext();
        db.ApiTokens.Add(new ApiToken { User = await db.Users.SingleAsync(u => u.Id == g.UserId), UserId = g.UserId, Token = "wf_848_" + g.UserId, Name = "848 fixture" });
        await db.SaveChangesAsync();
        using var logs = new TestLogProvider();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddLogging(l => l.AddProvider(logs));
            services.AddSingleton(Mock.Of<ITripEditorGeocodeSearchService>());
            services.AddSingleton(Mock.Of<IIconColorProvider>());
            services.AddSingleton(Mock.Of<ITripMapThumbnailGenerator>());
            services.AddSingleton(Mock.Of<ICacheWarmupScheduler>());
        });
        using var client = app.GetTestClient();
        if (cookie) { UserConnectionTokenHttpTests.Cookie(client, app.Services, g.UserId); await UserConnectionTokenHttpTests.AddAntiforgeryAsync(client); }
        else client.DefaultRequestHeaders.Authorization = new("Bearer", "wf_848_" + g.UserId);
        var route = $"/api/trips/{g.TripId}/{(cookie ? "editor/" : "")}regions/{g.SourceId}";
        var previewPath = route + "/deletion-preview" + (cookie ? "?contract=" + TripMetadataIdentity.StructureVersion : "");
        using var response = await client.PostAsJsonAsync(previewPath, new { places = "keep", areas = "keep" });
        Assert.True(response.StatusCode == HttpStatusCode.OK, string.Join("\n", logs.Entries.Select(l => l.Exception?.Message))); Assert.True(response.Headers.CacheControl?.NoStore);
        var preview = (await response.Content.ReadFromJsonAsync<RegionDeletionPreviewDto>())!;
        Assert.Equal("create", preview.Destination.Mode);
        Assert.False(await db.Regions.AnyAsync(r => r.Id == preview.Destination.RegionId));
        if (cookie)
        {
            var body = JsonSerializer.Serialize(new { receiptContract = TripMetadataIdentity.StructureVersion,
                places = "keep", areas = "keep", confirmationToken = preview.Confirmation.Token });
            var path = route + "/deletion/receipt";
            var permit = await TripMetadataReceiptHttp.IssueTypedAsync(client, RegionDeletionIdentity(g), path, body);
            using var applied = await TripMetadataReceiptHttp.SendAsync(client, HttpMethod.Post, path, permit, body);
            var receipt = (await TripMetadataReceiptHttp.EvidenceAsync(applied, permit, "applied")).GetProperty("result");
            Assert.Equal(g.PlaceIds.Order(), receipt.GetProperty("movedPlaces").EnumerateArray().Select(row => row.GetProperty("id").GetGuid()));
            Assert.Equal(g.AreaIds.Order(), receipt.GetProperty("movedAreas").EnumerateArray().Select(row => row.GetProperty("id").GetGuid()));
            Assert.Empty(receipt.GetProperty("segmentEffects").GetProperty("deletedSegmentIds").EnumerateArray());
            Assert.Equal(preview.Destination.RegionId, receipt.GetProperty("destinationRegionId").GetGuid());
            return;
        }
        using var mutation = await client.PostAsJsonAsync(route + "/deletion", new { places = "keep", areas = "keep", confirmationToken = preview.Confirmation.Token });
        Assert.Equal(HttpStatusCode.OK, mutation.StatusCode);
        var result = (await mutation.Content.ReadFromJsonAsync<EditorMutationResult<RegionDeletionResultDto>>())!;
        Assert.True(result.Success); Assert.Equal(g.PlaceIds.Order(), result.Data.MovedPlaceIds); Assert.Equal(g.AreaIds.Order(), result.Data.MovedAreaIds);
        Assert.NotEmpty(result.Affected.Places); Assert.NotEmpty(result.Affected.Areas); Assert.Empty(result.DeletedIds.Segments);
    }

    /// <summary>A reply lost after a real commit cannot be reported as rollback or safely replayed.</summary>
    [PostgresFact]
    public async Task LostReplyAfterCommit_PreservesCommittedGraphWithoutSuccessReceipt()
    {
        var g = await RegionDeletionTestGraph.SeedAsync(fixture);
        await using var db = fixture.CreateContext();
        db.ApiTokens.Add(new ApiToken { User = await db.Users.SingleAsync(u => u.Id == g.UserId), UserId = g.UserId,
            Token = "wf_848_lost_" + g.UserId, Name = "848 lost reply fixture" });
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddSingleton(Mock.Of<ITripMapThumbnailGenerator>());
            services.AddSingleton(Mock.Of<ICacheWarmupScheduler>());
        }, host => host.Use(async (context, next) =>
        {
            await next(context);
            if (context.Request.Path.Value!.EndsWith("/deletion", StringComparison.Ordinal) && context.Response.StatusCode == 200)
                throw new IOException("Injected lost reply after real commit.");
        }));
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "wf_848_lost_" + g.UserId);
        var route = $"/api/trips/{g.TripId}/regions/{g.SourceId}";
        var preview = await (await client.PostAsJsonAsync(route + "/deletion-preview", new { places = "keep", areas = "keep" }))
            .Content.ReadFromJsonAsync<RegionDeletionPreviewDto>();
        var failure = await Record.ExceptionAsync(async () =>
        {
            using var response = await client.PostAsJsonAsync(route + "/deletion", new { places = "keep", areas = "keep", confirmationToken = preview!.Confirmation.Token });
            await response.Content.ReadAsStringAsync();
        });
        Assert.NotNull(failure);
        await using var verify = fixture.CreateContext();
        Assert.False(await verify.Regions.AnyAsync(r => r.Id == g.SourceId));
        Assert.Equal(2, await verify.Areas.CountAsync(a => g.AreaIds.Contains(a.Id) && a.RegionId == g.DestinationId));
    }

    /// <summary>Cookie-only bearer requests, missing antiforgery, wrong owner and role never acquire authority.</summary>
    [PostgresFact]
    public async Task AuthorityAndStrictRequestBoundaries()
    {
        var g = await RegionDeletionTestGraph.SeedAsync(fixture);
        await using var db = fixture.CreateContext();
        using var logs = new TestLogProvider();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddLogging(l => l.AddProvider(logs));
            services.AddSingleton(Mock.Of<ITripEditorGeocodeSearchService>());
            services.AddSingleton(Mock.Of<IIconColorProvider>());
            services.AddSingleton(Mock.Of<ITripMapThumbnailGenerator>());
            services.AddSingleton(Mock.Of<ICacheWarmupScheduler>());
        });
        using var client = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(client, app.Services, g.UserId);
        var route = $"/api/trips/{g.TripId}/editor/regions/{g.SourceId}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"/api/trips/{g.TripId}/regions/{g.SourceId}/deletion-preview", new { places = "keep", areas = "keep" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route + "/deletion-preview", new { places = "keep", areas = "keep" })).StatusCode);
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(client);
        foreach (var body in new[] { "{}", "{\"places\":\"keep\"}", "{\"places\":0,\"areas\":\"keep\"}", "{\"places\":\"keep\",\"areas\":\"keep\",\"extra\":1}" })
        {
            using var invalid = await client.PostAsync(route + "/deletion-preview", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("region-deletion-request-invalid", (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        using var superseded = await client.PostAsJsonAsync(route + "/deletion", new { places = "delete", areas = "delete" });
        Assert.Equal(HttpStatusCode.Conflict, superseded.StatusCode);
        Assert.Equal("trip-write-upgrade-required", (await superseded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var protectedRoute = route + "/deletion/receipt";
        var missing = JsonSerializer.Serialize(new { receiptContract = TripMetadataIdentity.StructureVersion, places = "delete", areas = "delete" });
        var missingPermit = await TripMetadataReceiptHttp.IssueTypedAsync(client, RegionDeletionIdentity(g), protectedRoute, missing);
        using var missingReply = await TripMetadataReceiptHttp.SendAsync(client, HttpMethod.Post, protectedRoute, missingPermit, missing);
        Assert.Equal("mutation-review-required", (await TripMetadataReceiptHttp.EvidenceAsync(missingReply, missingPermit,
            "terminalUnapplied", 409)).GetProperty("code").GetString());
        var tampered = JsonSerializer.Serialize(new { receiptContract = TripMetadataIdentity.StructureVersion,
            places = "keep", areas = "keep", confirmationToken = "tampered" });
        var tamperedPermit = await TripMetadataReceiptHttp.IssueTypedAsync(client, RegionDeletionIdentity(g), protectedRoute, tampered);
        using var tamperedReply = await TripMetadataReceiptHttp.SendAsync(client, HttpMethod.Post, protectedRoute, tamperedPermit, tampered);
        Assert.Equal("region-deletion-preview-stale", (await TripMetadataReceiptHttp.EvidenceAsync(tamperedReply, tamperedPermit,
            "terminalUnapplied", 409)).GetProperty("code").GetString());
        var other = await fixture.CreateUserAsync();
        client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        UserConnectionTokenHttpTests.Cookie(client, app.Services, other.Id); await UserConnectionTokenHttpTests.AddAntiforgeryAsync(client);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(route + "/deletion-preview", new { places = "keep", areas = "keep" })).StatusCode);
        client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        UserConnectionTokenHttpTests.Cookie(client, app.Services, g.UserId, "Admin"); await UserConnectionTokenHttpTests.AddAntiforgeryAsync(client);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(route + "/deletion-preview", new { places = "keep", areas = "keep" })).StatusCode);
        Assert.True(await db.Regions.AnyAsync(r => r.Id == g.SourceId));
    }

    /// <summary>Binds the retained confirmation to the same owned Region and Web adapter.</summary>
    private static TripMetadataIdentity RegionDeletionIdentity(RegionDeletionTestGraph graph) =>
        new(TripMetadataIdentity.StructureVersion, graph.UserId, "web", "region-delete", graph.TripId, "", Guid.Empty, default)
        { Target = new("region", graph.SourceId) };
}
