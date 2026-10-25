using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Tests.Models;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class PlaceConditionalPostgresTests
{
    /// <summary>The delivered conservative Region consent and new structural originals reject intervening moves/orders in either save order.</summary>
    [PostgresTheory]
    [InlineData("move", false)]
    [InlineData("move", true)]
    [InlineData("place-order", false)]
    [InlineData("place-order", true)]
    [InlineData("destination-order", false)]
    [InlineData("destination-order", true)]
    [InlineData("region-order", false)]
    [InlineData("region-order", true)]
    public async Task RegionDeletion_ProtectedWriterIntegration(string operation, bool deleteFirst)
    {
        var g = await RegionDeletionTestGraph.SeedAsync(fixture);
        await using var db = fixture.CreateContext(); await AddCredentialAsync(db, g.UserId);
        var place = await db.Places.Include(p => p.Region).SingleAsync(p => p.Id == g.PlaceIds[0]);
        await using var app = await StartAsync(db); using var web = await WebAsync(app, g.UserId); using var bearer = Bearer(app, g.UserId);
        var deletionPath = $"/api/trips/{g.TripId}/{(deleteFirst ? "" : "editor/")}regions/{g.SourceId}";
        var deleting = deleteFirst ? bearer : web;
        using var discovery = await deleting.PostAsJsonAsync(deletionPath + "/deletion-preview", new { places = "keep", areas = "keep" });
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        var preview = (await discovery.Content.ReadFromJsonAsync<RegionDeletionPreviewDto>())!;
        var ordering = operation == "destination-order" ? bearer : web;
        string path;
        Dictionary<string, object?> request;
        if (operation == "move")
        {
            path = StructurePath(place, true, "move"); request = await MoveRequestAsync(place, g.DestinationId!.Value, true);
        }
        else if (operation == "region-order")
        {
            path = $"/api/trips/{g.TripId}/editor/regions/order";
            var regions = await db.Regions.AsNoTracking().Where(r => r.TripId == g.TripId).OrderBy(r => r.Id).ToArrayAsync();
            var normal = regions.Where(r => r.Name != "Unassigned Places").ToArray();
            var canonical = regions.Single(r => r.Name == "Unassigned Places");
            request = new() { ["expected"] = new { regions = normal.Select(r => new { r.Id, r.DisplayOrder, r.Name, notes = RichNotes.Normalize(r.Notes),
                r.CoverImageUrl, center = r.Center == null ? null : new { latitude = r.Center.Y, longitude = r.Center.X } }).ToArray(), canonical = new { canonical.Id, canonical.DisplayOrder } },
                ["regionIds"] = normal.Select(r => r.Id).Reverse().ToArray() };
        }
        else
        {
            var regionId = operation == "destination-order" ? g.DestinationId!.Value : g.SourceId;
            path = OrderPath(g.TripId, regionId, operation != "destination-order");
            var places = await db.Places.AsNoTracking().Where(p => p.RegionId == regionId).OrderBy(p => p.Id).ToArrayAsync();
            request = new() { ["expected"] = new { places = places.Select(Material).ToArray() }, ["placeIds"] = places.Select(p => p.Id).Reverse().ToArray() };
        }
        using var prepare = await ordering.PostAsJsonAsync(path + "/prepare", request);
        Assert.True(prepare.StatusCode == HttpStatusCode.OK, await prepare.Content.ReadAsStringAsync());
        request["conditionToken"] = (await prepare.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("conditionToken").GetString(); request["reviewed"] = true;
        Task<HttpResponseMessage> Delete() => deleting.PostAsJsonAsync(deletionPath + "/deletion", new { places = "keep", areas = "keep", confirmationToken = preview.Confirmation.Token });
        Task<HttpResponseMessage> Write() => ordering.PutAsJsonAsync(path, request);
        using var first = deleteFirst ? await Delete() : await Write();
        Assert.True(first.StatusCode == HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        var graph = await GraphStateAsync(g.TripId); var rows = await StoredStructureRowsAsync(g.TripId);
        using var second = deleteFirst ? await Write() : await Delete();
        Assert.True(second.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound, await second.Content.ReadAsStringAsync());
        Assert.Equal(graph, await GraphStateAsync(g.TripId)); Assert.Equal(rows, await StoredStructureRowsAsync(g.TripId));
        if (deleteFirst && Environment.GetEnvironmentVariable("WAYFARER_DELETION_EVIDENCE") is { Length: > 0 } directory)
            await File.WriteAllTextAsync(Path.Combine(directory, $"region-{operation}.json"), JsonSerializer.Serialize(new { preview,
                result = await first.Content.ReadFromJsonAsync<JsonElement>() }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    /// <summary>Region integration now rechecks active account and incoming credential after a physical Region wait.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegionDeletion_CurrentAuthorityDuringWait(bool revokeAccount)
    {
        var g = await RegionDeletionTestGraph.SeedAsync(fixture, places: false, areas: false);
        await using var db = fixture.CreateContext(); await AddCredentialAsync(db, g.UserId);
        var probe = new PlaceProbe("Regions"); await using var app = await StartAsync(db, [probe]); using var client = Bearer(app, g.UserId);
        var path = $"/api/trips/{g.TripId}/regions/{g.SourceId}";
        var preview = (await (await client.PostAsJsonAsync(path + "/deletion-preview", new { places = "keep", areas = "keep" })).Content.ReadFromJsonAsync<RegionDeletionPreviewDto>())!;
        await using var blocker = fixture.CreateContext(); await using var transaction = await blocker.Database.BeginTransactionAsync();
        // This fixture changes account/token rows only; holding a Region creates no lock-order inversion.
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Regions\" WHERE \"Id\" = {g.SourceId} FOR UPDATE");
        var pending = client.PostAsJsonAsync(path + "/deletion", new { places = "keep", areas = "keep", confirmationToken = preview.Confirmation.Token });
        await WaitForLockAsync(probe, 0);
        if (revokeAccount) await blocker.Users.Where(u => u.Id == g.UserId).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false));
        else await blocker.ApiTokens.Where(t => t.UserId == g.UserId).ExecuteDeleteAsync();
        await transaction.CommitAsync();
        var before = await StoredStructureRowsAsync(g.TripId);
        using var response = await pending; Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(before, await StoredStructureRowsAsync(g.TripId));
    }
}
